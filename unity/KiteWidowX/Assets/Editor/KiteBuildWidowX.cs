using System.Collections.Generic;
using Unity.Robotics.UrdfImporter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Kite
{
    public static class KiteBuildWidowX
    {
        private const string UrdfPath = "Assets/URDF/wx250s/wx250s.urdf";
        private const string ScenePath = "Assets/Scenes/KiteWidowX.unity";
        private const int ImageSize = 224;

        [MenuItem("Kite/Build WidowX Scene")]
        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            BuildEnvironment();

            var settings = ImportSettings.DefaultSettings();
            settings.chosenAxis = ImportSettings.axisType.yAxis;
            settings.convexMethod = ImportSettings.convexDecomposer.unity;
            var robot = UrdfRobotExtensions.CreateRuntime(UrdfPath, settings);
            if (robot == null)
            {
                Debug.LogError("[Kite] URDF import failed");
                return;
            }
            robot.name = "wx250s";
            robot.transform.position = Vector3.zero;

            var joints = new List<ArticulationBody>();
            foreach (var body in robot.GetComponentsInChildren<ArticulationBody>())
            {
                if (body.jointType != ArticulationJointType.RevoluteJoint) continue;
                if (body.name.EndsWith("gripper_prop_link")) continue;
                joints.Add(body);
            }
            joints.Sort((a, b) => Depth(a.transform).CompareTo(Depth(b.transform)));
            Debug.Log("[Kite] joints: " + string.Join(", ", joints.ConvertAll(j => j.name)));

            BuildCameras(robot, joints);
            ApplyPose(joints);
            AttachAgent(robot, joints);
            System.IO.Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[Kite] scene saved: " + ScenePath);
        }

        private static void ApplyPose(List<ArticulationBody> joints)
        {
            var pose = new[] { 0f, -40f, 55f, 0f, -25f, 0f };
            for (int i = 0; i < joints.Count && i < pose.Length; i++)
            {
                var drive = joints[i].xDrive;
                drive.target = pose[i] * Mathf.Deg2Rad;
                drive.stiffness = 20000f;
                drive.damping = 500f;
                drive.forceLimit = 60f;
                joints[i].xDrive = drive;
            }
        }

        private static void BuildEnvironment()
        {
            var table = GameObject.CreatePrimitive(PrimitiveType.Cube);
            table.name = "table";
            table.transform.position = new Vector3(0f, -0.02f, 0.22f);
            table.transform.localScale = new Vector3(0.9f, 0.04f, 0.72f);
            table.GetComponent<Renderer>().sharedMaterial = MakeMaterial("table", new Color(0.38f, 0.52f, 0.47f));

            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "floor";
            floor.transform.position = new Vector3(0f, -0.8f, 0.22f);
            floor.transform.localScale = new Vector3(6f, 0.04f, 6f);
            floor.GetComponent<Renderer>().sharedMaterial = MakeMaterial("floor", new Color(0.55f, 0.56f, 0.58f));

            var target = GameObject.CreatePrimitive(PrimitiveType.Cube);
            target.name = "cube_red";
            target.transform.position = new Vector3(0.26f, 0.02f, 0.34f);
            target.transform.localScale = new Vector3(0.04f, 0.04f, 0.04f);
            target.GetComponent<Renderer>().sharedMaterial = MakeMaterial("cube_red", new Color(0.78f, 0.19f, 0.16f));

            var light = new GameObject("key_light");
            var lightComp = light.AddComponent<Light>();
            lightComp.type = LightType.Directional;
            lightComp.intensity = 1.1f;
            lightComp.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(55f, -40f, 0f);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.43f, 0.46f);
        }

        private static Material MakeMaterial(string name, Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }
            return new Material(shader) { name = name, color = color };
        }

        private static void BuildCameras(GameObject robot, List<ArticulationBody> joints)
        {
            var front = new GameObject("cam_front");
            front.transform.SetParent(robot.transform, false);
            front.transform.position = new Vector3(-0.55f, 0.62f, -0.50f);
            front.transform.LookAt(new Vector3(0.16f, 0.04f, 0.26f), Vector3.up);
            AttachCamera(front, "front");

            Transform gripper = null;
            foreach (var body in joints)
            {
                if (body.name.EndsWith("gripper_link"))
                {
                    gripper = body.transform;
                }
            }

            var wrist = new GameObject("cam_wrist");
            if (gripper != null)
            {
                wrist.transform.SetParent(gripper, false);
                wrist.transform.localPosition = new Vector3(0.02f, 0.07f, -0.04f);
                wrist.transform.localRotation = Quaternion.Euler(45f, 90f, 0f);
            }
            else
            {
                wrist.transform.position = new Vector3(0.3f, 0.35f, 0.1f);
            }
            AttachCamera(wrist, "wrist");
        }

        private static void AttachAgent(GameObject robot, List<ArticulationBody> joints)
        {
            var agent = robot.AddComponent<KiteWidowXAgent>();
            joints.Sort((a, b) => Depth(a.transform).CompareTo(Depth(b.transform)));
            agent.armJoints = joints.ToArray();
            agent.camFront = FindCamera(robot, "cam_front");
            agent.camWrist = FindCamera(robot, "cam_wrist");
            foreach (var body in robot.GetComponentsInChildren<ArticulationBody>())
            {
                if (body.name.EndsWith("gripper_link")) agent.eeLink = body.transform;
                if (body.name.EndsWith("left_finger_link")) agent.leftFinger = body;
                else if (body.name.EndsWith("right_finger_link")) agent.rightFinger = body;
            }
            Debug.Log($"[Kite] agent joints={agent.armJoints.Length} front={(agent.camFront != null)} wrist={(agent.camWrist != null)} left={(agent.leftFinger != null)} right={(agent.rightFinger != null)}");
        }

        private static Camera FindCamera(GameObject robot, string name)
        {
            foreach (var cam in robot.GetComponentsInChildren<Camera>())
            {
                if (cam.name == name) return cam;
            }
            return null;
        }

        private static int Depth(Transform t)
        {
            int d = 0;
            while (t.parent != null) { t = t.parent; d++; }
            return d;
        }

        private static void AttachCamera(GameObject host, string tag)
        {
            var cam = host.AddComponent<Camera>();
            cam.fieldOfView = 55f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 20f;
            cam.targetTexture = new RenderTexture(ImageSize, ImageSize, 24, RenderTextureFormat.ARGB32)
            {
                name = "rt_" + tag
            };
        }
    }
}







