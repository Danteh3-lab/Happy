using HappyBot.Overlay;

namespace HappyBot.Update;

/// <summary>Small window shown while an update downloads, before the deck opens.</summary>
internal sealed class UpdateProgressForm : Form
{
    private const int Steps = 1000;
    private readonly ProgressBar _bar;

    public UpdateProgressForm(Version version)
    {
        Text = "Updating DANBOT";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(380, 96);
        BackColor = OverlayTheme.Panel;
        ForeColor = OverlayTheme.Text;
        Padding = new Padding(20);

        var label = new Label
        {
            Text = $"Updating DANBOT to v{version}…",
            Dock = DockStyle.Top,
            Height = 32,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold)
        };
        _bar = new ProgressBar { Dock = DockStyle.Bottom, Height = 8, Maximum = Steps, Style = ProgressBarStyle.Continuous };
        Controls.Add(_bar);
        Controls.Add(label);
    }

    public void SetProgress(double fraction)
    {
        if (!IsDisposed) _bar.Value = (int)Math.Clamp(fraction * Steps, 0, Steps);
    }
}
