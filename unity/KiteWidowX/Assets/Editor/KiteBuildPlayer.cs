using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Kite
{
    public static class KiteBuildPlayer
    {
        private const string ScenePath = "Assets/Scenes/KiteWidowX.unity";
        private const string OutputDir = "Build/KiteWidowX";

        [MenuItem("Kite/Rebuild Scene + Player")]
        public static void RebuildAll()
        {
            KiteBuildWidowX.Build();
            BuildWindowsPlayer();
        }

        [MenuItem("Kite/Build Windows Player")]
        public static void BuildWindowsPlayer()
        {
            PlayerSettings.runInBackground = true;
            PlayerSettings.defaultScreenWidth = 640;
            PlayerSettings.defaultScreenHeight = 480;

            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = OutputDir + "/KiteWidowX.exe",
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            Debug.Log($"[Kite] player build: {report.summary.result}, {report.summary.totalSize} bytes, errors={report.summary.totalErrors}");
        }
    }
}

