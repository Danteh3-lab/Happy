using System.Media;
using HappyBot.App;

namespace HappyBot.Shell;

internal sealed class WinFormsSaveFilePicker : ISaveFilePicker
{
    private readonly IWin32Window _owner;

    public WinFormsSaveFilePicker(IWin32Window owner)
    {
        _owner = owner;
    }

    public string PickSavePath(string title, string filter, string suggestedName)
    {
        using var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = suggestedName,
            OverwritePrompt = true
        };
        return dialog.ShowDialog(_owner) == DialogResult.OK ? dialog.FileName : null;
    }
}

/// <summary>Plays the optional buttonclick/buttonunclick WAVs next to the exe.</summary>
internal sealed class WavSoundCue : ISoundCue
{
    public void Click() => Play("buttonclick");

    public void Unclick() => Play("buttonunclick");

    public void Beep() => SystemSounds.Beep.Play();

    private static void Play(string name)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, name + ".wav");
            if (File.Exists(path))
                using (var player = new SoundPlayer(path)) player.Play();
        }
        catch
        {
        }
    }
}
