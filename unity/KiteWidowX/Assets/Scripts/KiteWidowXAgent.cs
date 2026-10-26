using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Kite
{
    /// <summary>
    /// TCP bridge between the Unity WidowX scene and the Kite VLA loop.
    /// Line protocol: "OBS" -> observation JSON, "ACT &lt;6 rad&gt; &lt;grip 0..1&gt;" -> applies targets.
    /// </summary>
    public class KiteWidowXAgent : MonoBehaviour
    {
        private const int Port = 7777;
        private const int ImageSize = 224;

        [Tooltip("Arm joints in URDF order (waist, shoulder, elbow, forearm_roll, wrist_angle, wrist_rotate).")]
        public ArticulationBody[] armJoints;

        public ArticulationBody leftFinger;
        public Transform eeLink;
        public ArticulationBody rightFinger;

        public Camera camFront;
        public Camera camWrist;

        [Tooltip("Ready pose in degrees.")]
        public float[] readyPose = { 0f, -40f, 55f, 0f, -25f, 0f };

        public float gripperOpen = 0.037f;
        public float gripperClosed = 0.016f;

        private struct Request
        {
            public string Line;
            public NetworkStream Stream;
        }

        private readonly ConcurrentQueue<Request> _queue = new ConcurrentQueue<Request>();
        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _running;
        private readonly List<ArticulationBody> _joints = new List<ArticulationBody>();
        private readonly float[] _targets = new float[6];
        private readonly Dictionary<string, string> _hud = new Dictionary<string, string>();
        private float _hudAt;
        private int _actCount;
        private float _fps;
        private float _gripTarget;

        private void Awake()
        {
            Application.runInBackground = true;
            if (eeLink == null)
            {
                foreach (var body in GetComponentsInChildren<ArticulationBody>())
                {
                    if (body.name.EndsWith("gripper_link")) eeLink = body.transform;
                }
            }
            if (armJoints == null || armJoints.Length == 0)
            {
                CollectJoints();
            }
            _joints.AddRange(armJoints);
            DisableImporterController();
            ConfigureDrives();
            ApplyReadyPose();
            EnsureDisplayCamera();
        }

        private void DisableImporterController()
        {
            foreach (var mb in GetComponentsInChildren<MonoBehaviour>())
            {
                if (mb != null && mb.GetType().Name == "Controller")
                {
                    mb.enabled = false;
                }
            }
        }

        private void FixedUpdate()
        {
            for (int i = 0; i < _joints.Count && i < _targets.Length; i++)
            {
                var drive = _joints[i].xDrive;
                drive.target = _targets[i] * Mathf.Rad2Deg;
                _joints[i].xDrive = drive;
            }
            ApplyGripperDrives();
        }

        private void EnsureDisplayCamera()
        {
            if (!Application.isEditor && camFront != null)
            {
                var display = new GameObject("cam_display").AddComponent<Camera>();
                display.transform.SetPositionAndRotation(camFront.transform.position, camFront.transform.rotation);
                display.fieldOfView = camFront.fieldOfView;
                display.nearClipPlane = camFront.nearClipPlane;
                display.farClipPlane = camFront.farClipPlane;
                display.targetTexture = null;
                display.depth = -10f;
            }
        }

        private void Start()
        {
            _running = true;
            _thread = new Thread(Serve) { IsBackground = true, Name = "KiteWidowXBridge" };
            _thread.Start();
            Debug.Log($"[Kite] bridge listening on {Port}");
        }

        private void OnDestroy()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
        }

        private void CollectJoints()
        {
            var found = new List<ArticulationBody>();
            foreach (var body in GetComponentsInChildren<ArticulationBody>())
            {
                var name = body.name.Replace("wx250s/", string.Empty);
                if (name == "gripper_link") eeLink = body.transform;
                if (name == "left_finger_link") leftFinger = body;
                else if (name == "right_finger_link") rightFinger = body;
                else if (body.jointType == ArticulationJointType.RevoluteJoint) found.Add(body);
            }
            // Depth-first order matches the URDF chain (waist -> ... -> wrist_rotate).
            found.Sort((a, b) => Depth(a.transform).CompareTo(Depth(b.transform)));
            armJoints = found.ToArray();
        }

        private static int Depth(Transform t)
        {
            int d = 0;
            while (t.parent != null) { t = t.parent; d++; }
            return d;
        }

        private void ConfigureDrives()
        {
            foreach (var body in GetComponentsInChildren<ArticulationBody>())
            {
                bool isRoot = body.transform.parent == null || body.transform.parent.GetComponentInParent<ArticulationBody>() == null;
                if (isRoot)
                {
                    body.immovable = true;
                    body.useGravity = false;
                }
            }
            foreach (var body in _joints)
            {
                var drive = body.xDrive;
                drive.stiffness = 20000f;
                drive.damping = 500f;
                drive.forceLimit = 60f;
                if (Mathf.Approximately(drive.lowerLimit, 0f) && Mathf.Approximately(drive.upperLimit, 0f))
                {
                    drive.lowerLimit = -180f;
                    drive.upperLimit = 180f;
                }
                body.xDrive = drive;
            }
        }

        public void ApplyReadyPose()
        {
            for (int i = 0; i < _joints.Count && i < readyPose.Length; i++)
            {
                SetJoint(i, readyPose[i] * Mathf.Deg2Rad);
            }
            SetGripper(0f);
        }

        private void SetJoint(int index, float radians)
        {
            if (index < 0 || index >= _targets.Length) return;
            _targets[index] = radians;
            if (index >= _joints.Count) return;
            var body = _joints[index];
            var drive = body.xDrive;
            drive.target = radians * Mathf.Rad2Deg;
            body.xDrive = drive;
        }

        private void SetGripper(float closed01)
        {
            _gripTarget = Mathf.Clamp01(closed01);
            _actCount++;
            ApplyGripperDrives();
        }

        private void ApplyGripperDrives()
        {
            float target = Mathf.Lerp(gripperOpen, gripperClosed, _gripTarget);
            if (leftFinger != null)
            {
                var drive = leftFinger.xDrive;
                drive.target = target;
                drive.stiffness = 5000f;
                drive.damping = 200f;
                leftFinger.xDrive = drive;
            }
            if (rightFinger != null)
            {
                var drive = rightFinger.xDrive;
                drive.target = -target;
                drive.stiffness = 5000f;
                drive.damping = 200f;
                rightFinger.xDrive = drive;
            }
        }

        private void Serve()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Start();
                while (_running)
                {
                    var client = _listener.AcceptTcpClient();
                    client.NoDelay = true;
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    var pending = new StringBuilder();
                    while (_running && client.Connected)
                    {
                        int read;
                        try { read = stream.Read(buffer, 0, buffer.Length); }
                        catch { break; }
                        if (read <= 0) break;
                        pending.Append(Encoding.UTF8.GetString(buffer, 0, read));
                        while (true)
                        {
                            var text = pending.ToString();
                            int nl = text.IndexOf('\n');
                            if (nl < 0) break;
                            var line = text.Substring(0, nl).Trim();
                            pending.Remove(0, nl + 1);
                            _queue.Enqueue(new Request { Line = line, Stream = stream });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Kite] bridge stopped: " + ex.Message);
            }
        }

        private void LateUpdate()
        {
            if (camWrist != null && eeLink != null)
            {
                Vector3 fwd = eeLink.right;
                Vector3 up = transform.up;
                camWrist.transform.position = eeLink.position + up * 0.10f - fwd * 0.09f;
                camWrist.transform.rotation = Quaternion.LookRotation(fwd - up * 0.7f, up);
            }
        }

        private void Update()
        {
            SampleFps();
            while (_queue.TryDequeue(out var request))
            {
                string reply;
                try
                {
                    reply = Handle(request.Line);
                }
                catch (Exception ex)
                {
                    reply = "{\"ok\":false,\"error\":\"" + ex.Message.Replace("\"", "'") + "\"}";
                }
                if (request.Stream == null) continue;
                var bytes = Encoding.UTF8.GetBytes(reply + "\n");
                try { request.Stream.Write(bytes, 0, bytes.Length); } catch { }
            }
        }

        private string Handle(string line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "{\"ok\":false}";

            switch (parts[0].ToUpperInvariant())
            {
                case "OBS":
                    return Observation();
                case "ACT":
                    if (parts.Length < 8) return "{\"ok\":false,\"error\":\"need 6 joints + grip\"}";
                    for (int i = 0; i < 6; i++)
                    {
                        SetJoint(i, float.Parse(parts[i + 1], System.Globalization.CultureInfo.InvariantCulture));
                    }
                    SetGripper(float.Parse(parts[7], System.Globalization.CultureInfo.InvariantCulture));
                    return "{\"ok\":true}";
                case "HUD":
                    ParseHud(line);
                    return "{\"ok\":true}";
                case "STATE":
                    return State();
                case "INFO":
                    return Info();
                case "READY":
                    ApplyReadyPose();
                    return "{\"ok\":true}";
                default:
                    return "{\"ok\":false,\"error\":\"unknown command\"}";
            }
        }

        private void ParseHud(string line)
        {
            foreach (var part in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                _hud[part.Substring(0, eq)] = part.Substring(eq + 1);
            }
            _hudAt = Time.time;
        }

        private static float ParseFloat(string v, float fallback = 0f)
        {
            return float.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : fallback;
        }

        private static string Seconds(string ms)
        {
            return (ParseFloat(ms) / 1000f).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        }

        private string HudValue(string key, string fallback = "-")
        {
            return _hud.TryGetValue(key, out var v) ? v : fallback;
        }

        private void SampleFps()
        {
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f), 0.05f);
        }

        private void OnGUI()
        {
            float s = Mathf.Max(1f, Screen.height / 720f);
            int fs = Mathf.RoundToInt(13f * s);
            var box = new GUIStyle(GUI.skin.box);
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, new Color(0.04f, 0.05f, 0.06f, 0.78f));
            tex.Apply();
            box.normal.background = tex;

            var label = new GUIStyle(GUI.skin.label) { fontSize = fs, richText = true, alignment = TextAnchor.UpperLeft };
            label.normal.textColor = new Color(0.92f, 0.94f, 0.96f);

            float width = 360f * s;
            float height = 430f * s;
            var rect = new Rect(12f * s, 12f * s, width, height);
            GUI.Box(rect, GUIContent.none, box);

            var sb = new StringBuilder();
            sb.Append("<b>KITE · X-VLA · WidowX 250 · 闭环</b>\n");
            sb.Append("框架   ").Append(HudValue("backend")).Append("   流匹配步数 ").Append(HudValue("steps"))
              .Append("   chunk ").Append(HudValue("chunk")).Append('\n');
            float progress = ParseFloat(HudValue("progress", "0"));
            int cells = 18;
            int filled = Mathf.Clamp(Mathf.RoundToInt(progress * cells), 0, cells);
            string bar = new string('=', filled) + new string('-', cells - filled);
            string stage = HudValue("stage", "idle");
            string stageCn = stage == "encoder" ? "编码器" : stage == "denoise" ? "去噪" : stage == "done" ? "完成" : "待机";
            sb.Append("推理   [").Append(bar).Append("] ").Append(Mathf.RoundToInt(progress * 100f)).Append("%  ")
              .Append(stageCn).Append(' ').Append(HudValue("step")).Append('\n');
            sb.Append("过程   enc ").Append(HudValue("enc_ms")).Append(" ms | ")
              .Append(HudValue("step_ms")).Append(" ms/步 (").Append(HudValue("last3")).Append(")\n");
            sb.Append("耗时   已用 ").Append(Seconds(HudValue("elapsed_ms")))
              .Append(" s | 剩 ").Append(Seconds(HudValue("eta_ms"))).Append(" s\n");
            sb.Append("指令   ").Append(HudValue("instruction").Replace('_', ' ')).Append('\n');
            sb.Append("Token  ").Append(HudValue("tok")).Append(" ...（前 8 个）\n");
            sb.Append("输入   图像 ").Append(HudValue("views")).Append("×224×224   文本 ")
              .Append(HudValue("tokens")).Append(" tokens   设备 ").Append(HudValue("device")).Append('\n');
            sb.Append("本次   ").Append(HudValue("total_ms")).Append(" ms (enc ").Append(HudValue("enc_ms"))
              .Append(" + 10×").Append(HudValue("step_ms")).Append(" + post ").Append(HudValue("post_ms")).Append(")\n");
            sb.Append("chunk 耗时 ").Append(HudValue("e2e_ms")).Append(" ms   控制 10 Hz\n");
            sb.Append("通信   ").Append(HudValue("comm_ms")).Append(" ms 往返\n");
            sb.Append("模型输出  p=[").Append(HudValue("target")).Append("]  grip ").Append(HudValue("grip")).Append('\n');
            sb.Append("姿态误差  ").Append(HudValue("rot_err")).Append(" rad\n");
            sb.Append("关节(°)   ");
            for (int i = 0; i < _joints.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append((_joints[i].jointPosition[0] * Mathf.Rad2Deg).ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            }
            sb.Append('\n').Append("目标(°)   ");
            for (int i = 0; i < _targets.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append((_targets[i] * Mathf.Rad2Deg).ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            }
            sb.Append('\n').Append("末端      [");
            Transform hudEe = eeLink != null ? eeLink : transform;
            Vector3 hudPos = transform.InverseTransformPoint(hudEe.position);
            sb.Append(Fmt(hudPos.x)).Append(',').Append(Fmt(hudPos.y)).Append(',').Append(Fmt(hudPos.z)).Append("]\n");
            sb.Append("渲染 ").Append(_fps.ToString("F0")).Append(" fps   已执行 ").Append(_actCount).Append(" 动作");

            GUI.Label(new Rect(rect.x + 10f * s, rect.y + 8f * s, width - 20f * s, height), sb.ToString(), label);

            float thumb = 104f * s;
            float gap = 8f * s;
            float ty = rect.yMax - thumb - 8f * s;
            var cap = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(10f * s) };
            cap.normal.textColor = new Color(0.75f, 0.78f, 0.82f);
            if (camFront != null && camFront.targetTexture != null)
            {
                var fr = new Rect(rect.x + 10f * s, ty, thumb, thumb);
                GUI.DrawTexture(fr, camFront.targetTexture, ScaleMode.ScaleToFit, false);
                GUI.Label(new Rect(fr.x, fr.y - 13f * s, thumb, 12f * s), "cam_front 224²", cap);
            }
            int viewsUsed = Mathf.RoundToInt(ParseFloat(HudValue("views", "0")));
            if (viewsUsed >= 2 && camWrist != null && camWrist.targetTexture != null)
            {
                var wr = new Rect(rect.x + 10f * s + thumb + gap, ty, thumb, thumb);
                GUI.DrawTexture(wr, camWrist.targetTexture, ScaleMode.ScaleToFit, false);
                GUI.Label(new Rect(wr.x, wr.y - 13f * s, thumb, 12f * s), "cam_wrist 224²", cap);
            }
        }

        private string State()
        {
            var sb = new StringBuilder();
            sb.Append("{\"joints\":[");
            for (int i = 0; i < _joints.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Fmt(_joints[i].jointPosition[0]));
            }
            sb.Append("],\"grip\":");
            sb.Append(Fmt(leftFinger != null ? leftFinger.jointPosition[0] : 0f));
            Transform ee = eeLink != null ? eeLink : transform;
            Vector3 p = transform.InverseTransformPoint(ee.position);
            Quaternion q = Quaternion.Inverse(transform.rotation) * ee.rotation;
            sb.Append(",\"ee\":[").Append(Fmt(p.x)).Append(',').Append(Fmt(p.y)).Append(',').Append(Fmt(p.z)).Append(']');
            sb.Append(",\"ee_quat\":[").Append(Fmt(q.x)).Append(',').Append(Fmt(q.y)).Append(',')
                .Append(Fmt(q.z)).Append(',').Append(Fmt(q.w)).Append(']');
            Vector3 fwdDir = transform.InverseTransformDirection(eeLink != null ? eeLink.right : transform.right);
            sb.Append(",\"fwd\":[").Append(Fmt(fwdDir.x)).Append(',').Append(Fmt(fwdDir.y)).Append(',')
                .Append(Fmt(fwdDir.z)).Append("]}");
            return sb.ToString();
        }

        private string Info()
        {
            var sb = new StringBuilder();
            sb.Append("{\"armJoints\":").Append(armJoints == null ? -1 : armJoints.Length);
            sb.Append(",\"joints\":").Append(_joints.Count);
            sb.Append(",\"bodies\":[");
            for (int i = 0; i < _joints.Count; i++)
            {
                var b = _joints[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":\"").Append(b.name).Append("\"");
                sb.Append(",\"type\":\"").Append(b.jointType).Append("\"");
                sb.Append(",\"target\":").Append(b.xDrive.target.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(",\"stiff\":").Append(b.xDrive.stiffness.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)); sb.Append(",\"lim\":[").Append(b.xDrive.lowerLimit.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append(",").Append(b.xDrive.upperLimit.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append("]");
                sb.Append(",\"pos\":").Append(b.jointPosition[0].ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(",\"immovable\":").Append(b.immovable ? "true" : "false");
                sb.Append(",\"enabled\":").Append(b.enabled ? "true" : "false").Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private string Observation()
        {
            var sb = new StringBuilder();
            sb.Append("{\"joints\":[");
            for (int i = 0; i < _joints.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(_joints[i].jointPosition[0].ToString("F6", System.Globalization.CultureInfo.InvariantCulture));
            }
            sb.Append("],\"grip\":");
            float grip = leftFinger != null ? leftFinger.jointPosition[0] : 0f;
            sb.Append(grip.ToString("F6", System.Globalization.CultureInfo.InvariantCulture));
            Transform ee = eeLink != null ? eeLink : transform;
            Vector3 p = transform.InverseTransformPoint(ee.position);
            Quaternion q = Quaternion.Inverse(transform.rotation) * ee.rotation;
            sb.Append(",\"ee\":[").Append(Fmt(p.x)).Append(',').Append(Fmt(p.y)).Append(',').Append(Fmt(p.z)).Append(']');
            sb.Append(",\"ee_quat\":[").Append(Fmt(q.x)).Append(',').Append(Fmt(q.y)).Append(',').Append(Fmt(q.z)).Append(',').Append(Fmt(q.w)).Append(']');
            sb.Append(",\"images\":[\"");
            sb.Append(Encode(camFront));
            sb.Append("\",\"");
            sb.Append(Encode(camWrist));
            sb.Append("\"]}");
            return sb.ToString();
        }

        private static string Fmt(float v)
        {
            return v.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Encode(Camera cam)
        {
            if (cam == null) return string.Empty;
            var rt = cam.targetTexture;
            if (rt == null) return string.Empty;
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(ImageSize, ImageSize, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, ImageSize, ImageSize), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;
            var png = tex.EncodeToPNG();
            Destroy(tex);
            return Convert.ToBase64String(png);
        }
    }
}



























