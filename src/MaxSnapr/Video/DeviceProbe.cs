using System;
using System.Text;
using System.Threading.Tasks;

namespace MaxSnapr.Video;

/// <summary>Opens each input briefly and reports whether it works. No picture or sound is kept.</summary>
public static class DeviceProbe
{
    public static async Task<string> RunAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Micrófono predeterminado: " + (Devices.DefaultMicrophoneName() ?? "ninguno"));
        foreach (var m in Devices.Microphones()) sb.AppendLine("  micrófono: " + m.Name);
        using (var mic = LevelMonitor.Microphone(null))
        {
            float peak = 0;
            for (int i = 0; i < 15; i++) { await Task.Delay(100); peak = Math.Max(peak, mic?.Take() ?? 0); }
            sb.AppendLine(mic == null ? "Micrófono: no se pudo abrir" : $"Micrófono: abierto, pico {peak:0.000}");
        }
        using (var sys = LevelMonitor.SystemOutput())
            sb.AppendLine(sys == null ? "Audio del sistema: no se pudo abrir" : "Audio del sistema: abierto");

        var cams = await Devices.CamerasAsync();
        foreach (var c in cams) sb.AppendLine("  cámara: " + c.Name);
        foreach (var device in cams)
        {
            try
            {
                sb.AppendLine("Probando " + device.Name);
                using var cam = await WebcamSource.StartAsync(device.Id);
                string size = "sin fotogramas";
                for (int i = 0; i < 30 && size == "sin fotogramas"; i++)
                {
                    await Task.Delay(100);
                    cam.Use(b => size = $"{b.Width}×{b.Height}");
                }
                sb.AppendLine($"Cámara: abierta, {size}");
                foreach (var effect in new[] { CameraBackgrounds.None, CameraBackgrounds.BlurStrong, "preset:estudio" })
                {
                    cam.Background = effect;
                    await Task.Delay(800);
                    int n0 = cam.FramesReceived;
                    await Task.Delay(3000);
                    sb.AppendLine($"  {CameraBackgrounds.Describe(effect)}: {(cam.FramesReceived - n0) / 3.0:0.0} fps");
                }
            }
            catch (Exception ex) { sb.AppendLine($"Cámara: error {ex.GetType().Name}: {ex.Message}"); }
        }
        return sb.ToString();
    }
}
