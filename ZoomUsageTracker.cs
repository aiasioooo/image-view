namespace ImageViewerAutoscale;

public sealed class ZoomUsageTracker
{
    private readonly Queue<(DateTimeOffset Time, double Zoom, DisplayMode Mode)> _samples = new();

    public void RecordZoom(double zoom, DisplayMode mode)
    {
        _samples.Enqueue((DateTimeOffset.UtcNow, zoom, mode));
        Trim();
    }

    public void RecordMode(DisplayMode mode)
    {
        var zoom = _samples.LastOrDefault().Zoom;
        RecordZoom(zoom <= 0 ? 1.0 : zoom, mode);
    }

    public IEnumerable<DisplayMode> GetDesiredModes(DisplayMode selectedMode)
    {
        if (selectedMode.IsGenerated())
        {
            yield return selectedMode;
        }

        var recent = _samples.ToArray();
        var maxZoom = recent.Length == 0 ? 1.0 : recent.Max(sample => sample.Zoom);
        var mlWasUsed = recent.Any(sample => sample.Mode.IsExternalMl());

        if (maxZoom >= 1.4)
        {
            yield return DisplayMode.HighQuality2x;
        }

        if (maxZoom >= 2.6)
        {
            yield return DisplayMode.HighQuality4x;
        }

        if (mlWasUsed && maxZoom >= 1.6)
        {
            yield return DisplayMode.AnimeMl2x;
        }

        if (mlWasUsed && maxZoom >= 3.0)
        {
            yield return DisplayMode.AnimeMl4x;
        }
    }

    public IEnumerable<DisplayMode> GetNeighborModes(DisplayMode selectedMode)
    {
        var recent = _samples.ToArray();
        if (recent.Length == 0)
        {
            yield break;
        }

        var maxZoom = recent.Max(sample => sample.Zoom);
        if (selectedMode.IsExternalMl())
        {
            yield return selectedMode == DisplayMode.AnimeMl4x ? DisplayMode.AnimeMl2x : selectedMode;
            yield break;
        }

        if (maxZoom >= 1.6)
        {
            yield return DisplayMode.HighQuality2x;
        }
    }

    private void Trim()
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-45);
        while (_samples.Count > 64 || (_samples.TryPeek(out var sample) && sample.Time < cutoff))
        {
            _samples.Dequeue();
        }
    }
}
