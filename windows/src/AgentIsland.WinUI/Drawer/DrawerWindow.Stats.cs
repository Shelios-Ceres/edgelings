using System.Diagnostics;

namespace AgentIsland.WinUI.Drawer;

// AGENTISLAND_DEBUG_DRAWER_STATS: game-loop rate and process CPU for verifying the pacing.
internal sealed partial class DrawerWindow
{
    /// AGENTISLAND_DEBUG_DRAWER_STATS: game-loop rate and process CPU, once a
    /// second, for verifying the pacing.
    private sealed class DrawerStats(string path)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Process _process = Process.GetCurrentProcess();
        private TimeSpan _cpu = Process.GetCurrentProcess().TotalProcessorTime;
        private double _windowStart;
        private int _frames;
        private int _steps;
        private double _updateMs;
        private double _drawMs;

        public void Drew(double ms) => _drawMs += ms;

        public void Frame(int steps, double updateMs)
        {
            _frames++;
            _steps += steps;
            _updateMs += updateMs;
            var now = _clock.Elapsed.TotalSeconds;
            if (now - _windowStart < 1) return;
            _process.Refresh();
            var cpu = _process.TotalProcessorTime;
            var span = now - _windowStart;
            try
            {
                File.AppendAllText(path,
                    $"{now:F1}s loop={_frames / span:F0}/s steps={_steps / span:F0}/s update={_updateMs / Math.Max(1, _frames):F2}ms draw={_drawMs / Math.Max(1, _frames):F2}ms cpu={(cpu - _cpu).TotalSeconds / span * 100:F1}%core{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
            _frames = _steps = 0;
            _updateMs = _drawMs = 0;
            _windowStart = now;
            _cpu = cpu;
        }
    }
}
