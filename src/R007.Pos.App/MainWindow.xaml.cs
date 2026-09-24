using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using R007.Pos.Devices.Input;
using R007.Pos.ViewModels;

namespace R007.Pos.App;

/// <summary>
/// Hosts the shell. Also turns keyboard-wedge devices (USB/BT barcode scanners, NFC card readers that "type" their
/// payload) into scan events: a burst of characters arriving faster than a person types, ended by Enter, goes to
/// <see cref="ShellViewModel.HandleWedgeInputAsync"/> (a card on the login screen, a barcode elsewhere) instead of into
/// whatever text box has focus. Every input also resets the idle-lock timer.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly KeyboardWedgeDecoder _decoder;
    private string _leaked = string.Empty;

    public MainWindow(ShellViewModel shell, int wedgeMaxKeyIntervalMs)
    {
        _shell = shell;
        _decoder = new KeyboardWedgeDecoder(TimeSpan.FromMilliseconds(Math.Max(10, wedgeMaxKeyIntervalMs)));
        DataContext = shell;
        InitializeComponent();
        PreviewTextInput += OnPreviewTextInput;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += (_, _) => _shell.NotifyActivity();
        PreviewTouchDown += (_, _) => _shell.NotifyActivity();
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        _shell.NotifyActivity();
        foreach (var ch in e.Text)
        {
            var scan = _decoder.Feed(ch, DateTimeOffset.UtcNow);
            if (scan is not null)
            {
                return; // Enter completes scans in OnPreviewKeyDown; text never carries them
            }
        }

        if (_decoder.IsBursting)
        {
            e.Handled = true; // it is a scanner: keep the payload out of the focused text box
        }
        else
        {
            _leaked += e.Text; // the first characters of a burst arrive before we can tell (remembered so they can be removed)
            if (_leaked.Length > 3)
            {
                _leaked = _leaked[^3..];
            }
        }
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        _shell.NotifyActivity();
        if (e.Key is not (Key.Enter or Key.Return))
        {
            return;
        }

        var scan = _decoder.Feed('\r', DateTimeOffset.UtcNow);
        if (scan is null)
        {
            _leaked = string.Empty;
            return;
        }

        e.Handled = true; // this Enter belongs to the scanner, not to a default button or key binding
        StripLeakedCharacters(scan);
        await _shell.HandleWedgeInputAsync(scan);
    }

    private void StripLeakedCharacters(string scan)
    {
        var leaked = _leaked;
        _leaked = string.Empty;
        if (leaked.Length == 0 || Keyboard.FocusedElement is not TextBox box)
        {
            return;
        }

        var prefix = scan[..Math.Min(2, scan.Length)]; // at most two characters slip through before a burst is recognised
        if (box.Text.EndsWith(prefix, StringComparison.Ordinal))
        {
            box.Text = box.Text[..^prefix.Length];
            box.CaretIndex = box.Text.Length;
        }
    }
}
