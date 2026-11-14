using Stratoscope.Core;

namespace Stratoscope.Windows;

/// <summary>Turns the monitors into what the panel, the readout and the icon show.</summary>
internal static class PanelModelBuilder
{
    public static PanelModel Panel(IList<ServerMonitor> monitors, int selected, DateTime now, PanelSettings? settings = null, HistorySmoothing smoothing = HistorySmoothing.Normal)
    {
        var model = new PanelModel { Now = now, VersionLabel = AppInfo.Label, Settings = settings, Smoothing = smoothing };
        foreach (var m in monitors) model.Tabs.Add(new TabInfo(m.Name, m.State, Display.Word(m.State)));
        model.Selected = Servers.Clamp(selected, monitors.Count);
        if (monitors.Count > 0)
        {
            var m = monitors[model.Selected];
            model.ServerName = m.Name; model.State = m.State; model.Snapshot = m.Snapshot; model.Error = m.Error; model.History = m.History;
        }
        return model;
    }

    public static List<ReadoutItem> Readout(IEnumerable<ServerMonitor> monitors)
    {
        var list = monitors.ToList();
        return list.Select(m => ReadoutItem.From(m, list.Count > 1)).ToList();      // the name follows the status only where several servers are shown
    }
}
