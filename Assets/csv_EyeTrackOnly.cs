using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using Tobii.Gaming;
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
using NativeApi = Tobii.GameIntegration.Net.TobiiGameIntegrationApi;
#endif

public class csv_EyeTrackOnly : MonoBehaviour
{
    [Header("Logging Control")]
    public bool enabledLogging = true;

    [Header("視線マーカー (UI Image の RectTransform)")]
    [SerializeField] private RectTransform e;

    [Header("ファイル名入力")]
    [SerializeField] private InputField filenameInputField;

    [Header("視線取得の診断 (1秒ごと)")]
    [SerializeField] private bool diagnosticsEnabled = true;

    private StreamWriter sw;
    private string path;
    private bool isLogging;
    private float t0;
    private Vector2 filteredPos;
    private bool hasFilteredPosition;
    private float nextDiagnosticTime;

    void Update()
    {
        GazePoint gp;
        try
        {
            // Poll the SDK even before CSV recording starts. Preview and acquisition
            // diagnostics must not depend on the filename/Start UI.
            gp = TobiiAPI.GetGazePoint();
        }
        catch (Exception exception)
        {
            if (diagnosticsEnabled && Time.realtimeSinceStartup >= nextDiagnosticTime)
            {
                nextDiagnosticTime = Time.realtimeSinceStartup + 1f;
                Debug.LogError("[EyeTrack] Gaze acquisition failed: " + exception.Message);
            }
            return;
        }

        bool recent = gp.IsRecent();
        Vector2 gazePos = gp.Screen;
        bool usable = gp.IsValid && recent &&
            IsFinite(gazePos.x) && IsFinite(gazePos.y);

        if (usable)
        {
            filteredPos = hasFilteredPosition
                ? Vector2.Lerp(filteredPos, gazePos, 0.5f)
                : gazePos;
            hasFilteredPosition = true;
            if (e != null)
                e.position = new Vector3(filteredPos.x, filteredPos.y, 0);
        }
        else
        {
            hasFilteredPosition = false;
        }

        ReportDiagnostics(gp, recent);

        if (!enabledLogging || !isLogging || sw == null) return;

        float t = Time.realtimeSinceStartup - t0;
        try
        {
            sw.WriteLine(usable
                ? string.Format(CultureInfo.InvariantCulture,
                    "{0:F6},{1:F3},{2:F3},{3:F3},{4:F3}",
                    t, gazePos.x, gazePos.y, filteredPos.x, filteredPos.y)
                : string.Format(CultureInfo.InvariantCulture, "{0:F6},NN,NN,NN,NN", t));
        }
        catch (Exception exception)
        {
            Debug.LogError("[csv_EyeTrackOnly] CSV write failed: " + exception.Message);
            StopAndSave();
        }
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private void ReportDiagnostics(GazePoint gp, bool recent)
    {
        if (!diagnosticsEnabled || Time.realtimeSinceStartup < nextDiagnosticTime) return;
        nextDiagnosticTime = Time.realtimeSinceStartup + 1f;
        try
        {
            Debug.Log("[EyeTrack] IsConnected=" + TobiiAPI.IsConnected +
                ", IsValid=" + gp.IsValid + ", IsRecent=" + recent +
                ", Screen=" + gp.Screen + ", Viewport=" + gp.Viewport +
                ", GazeTimestamp=" + gp.Timestamp +
                ", UnscaledTime=" + Time.unscaledTime +
                ", AppFocused=" + Application.isFocused +
                ", IsLogging=" + isLogging + ", MarkerAssigned=" + (e != null) +
                ", GameScreen=" + Screen.width + "x" + Screen.height +
                ReadSdkDiagnostics());
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[EyeTrack] Diagnostics failed: " + exception.Message);
        }
    }

    private static string ReadSdkDiagnostics()
    {
        string result = "";
        try
        {
            PropertyInfo hostProperty = typeof(TobiiAPI).GetProperty(
                "Host", BindingFlags.Static | BindingFlags.NonPublic);
            object host = hostProperty == null ? null : hostProperty.GetValue(null, null);
            result += ", Host=" + (host == null ? "Unavailable" : host.GetType().FullName);
            if (host != null)
            {
                PropertyInfo initialized = host.GetType().GetProperty("IsInitialized");
                result += ", HostInitialized=" + (initialized == null
                    ? "Unknown" : Convert.ToString(initialized.GetValue(host, null)));
                FieldInfo providerField = FindField(host.GetType(), "_gameViewBoundsProvider");
                object provider = providerField == null ? null : providerField.GetValue(host);
                FieldInfo hwndField = provider == null ? null : FindField(provider.GetType(), "_hwnd");
                object hwnd = hwndField == null ? null : hwndField.GetValue(provider);
                result += ", HWND=" + (hwnd is IntPtr
                    ? "0x" + ((IntPtr)hwnd).ToInt64().ToString("X") : "Unknown");
            }
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            bool initializedApi = NativeApi.IsApiInitialized();
            result += ", ApiInitialized=" + initializedApi +
                ", TrackerEnabled=" + NativeApi.IsTrackerEnabled() +
                ", NativeDll=" + NativeApi.LoadedDll;
            if (initializedApi)
            {
                var tracker = NativeApi.GetTrackerInfo();
                if (tracker == null)
                    result += ", SelectedTracker=None";
                else
                {
                    var rect = tracker.DisplayRectInOSCoordinates;
                    result += ", TrackerMonitor=" + tracker.MonitorNameInOS +
                        ", TrackerAttached=" + tracker.IsAttached +
                        ", DisplayRect=(" + rect.Left + "," + rect.Top + "," +
                        rect.Right + "," + rect.Bottom + ")";
                }
            }
#endif
        }
        catch (Exception exception)
        {
            result += ", SdkDiagnostics=" + exception.GetType().Name + ":" + exception.Message;
        }
        return result;
    }

    private static FieldInfo FindField(Type type, string name)
    {
        for (; type != null; type = type.BaseType)
        {
            FieldInfo field = type.GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) return field;
        }
        return null;
    }

    // Start button and Enter submission use the same file creation path.
    public void StartLogging()
    {
        if (filenameInputField == null)
        {
            Debug.LogWarning("[csv_EyeTrackOnly] Filename InputField is not assigned.");
            return;
        }
        MakeFile(filenameInputField.text);
    }

    public void MakeFile(Text filenameUI)
    {
        if (filenameUI != null) MakeFile(filenameUI.text);
    }

    public void MakeFile(string baseName)
    {
        if (!enabledLogging || isLogging) return;
        if (string.IsNullOrWhiteSpace(baseName))
        {
            Debug.LogWarning("[csv_EyeTrackOnly] Enter a filename before starting recording.");
            return;
        }

        baseName = baseName.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(invalid, '_');

        try
        {
            string dir = Path.Combine(Application.dataPath, "ExperimentData_2026", "EyeTrack_2026");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, baseName + "_EyeTrackOnly.csv");
            // Preserve earlier measurements when a filename is reused.
            if (File.Exists(path))
                path = Path.Combine(dir, baseName + "_" +
                    DateTime.Now.ToString("yyyyMMdd_HHmmssfff") + "_EyeTrackOnly.csv");
            sw = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
            sw.WriteLine("time,pos_x,pos_y,corrected_pos_x,corrected_pos_y");
            t0 = Time.realtimeSinceStartup;
            isLogging = true;
            Debug.Log("[csv_EyeTrackOnly] Start logging: " + path);
        }
        catch (Exception exception)
        {
            Debug.LogError("[csv_EyeTrackOnly] Cannot start CSV recording: " + exception.Message);
            StopAndSave();
        }
    }

    public void StopAndSave()
    {
        bool wasLogging = isLogging;
        isLogging = false;
        if (sw != null)
        {
            try { sw.Dispose(); }
            catch (Exception exception) { Debug.LogWarning("[csv_EyeTrackOnly] CSV close failed: " + exception.Message); }
            sw = null;
        }
        if (wasLogging) Debug.Log("[csv_EyeTrackOnly] Logging stopped.");
    }

    void OnApplicationQuit() { StopAndSave(); }
    void OnDestroy() { StopAndSave(); }
}

