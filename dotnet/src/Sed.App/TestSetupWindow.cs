using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Sed.Formats.Launching;

namespace Sed.App;

/// <summary>Independent executable and engine profiles for JK and MotS.</summary>
public sealed class TestSetupWindow : Window
{
    public TestSetupWindow(AppSettings settings)
    {
        Title = "Test Setup";
        Width = 700;
        Height = OperatingSystem.IsWindows() ? 500 : 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x22));
        var stack = new StackPanel { Margin = new Thickness(12), Spacing = 8 };
        var project = Field(settings.ProjectDir);
        stack.Children.Add(Label("Test project folder"));
        var projectRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        projectRow.Children.Add(project);
        var browseProject = new Button { Content = "Browse…" };
        browseProject.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                { Title = "Select the test project folder", AllowMultiple = false });
            if (folders.FirstOrDefault() is { } folder) project.Text = folder.Path.LocalPath;
        };
        projectRow.Children.Add(browseProject);
        stack.Children.Add(projectRow);
        stack.Children.Add(Hint("Save and Test writes loose episode and level files here, plus a GOB or GOO archive. An existing episode definition must reference the level. Short paths without spaces work best."));

        var jkExecutable = Field(settings.JediKnightTestExecutable);
        var motsExecutable = Field(settings.MysteriesTestExecutable);
        var jkEngine = Profile(settings.JediKnightTestEngine);
        var motsEngine = Profile(settings.MysteriesTestEngine);
        AddGame("Jedi Knight", jkExecutable, jkEngine);
        AddGame("Mysteries of the Sith", motsExecutable, motsEngine);
        stack.Children.Add(Hint("Automatic prefers OpenJKDF2, then the game's retail executable. Leave the executable blank to search the game folder. For a renamed executable, choose its engine explicitly. Testing opens the game's menus with your project loaded."));

        TextBox? command = null;
        if (!OperatingSystem.IsWindows())
        {
            stack.Children.Add(Label("Custom shell command (Wine / CrossOver)"));
            command = Field(settings.TestCommand);
            stack.Children.Add(command);
            stack.Children.Add(Hint("Existing non-Windows command templates remain available: {project} {gob} {game} {gameexe} {levelname}. The Windows launcher uses executable profiles directly."));
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12), Spacing = 6 };
        var save = new Button { Content = "Save" };
        save.Click += (_, _) =>
        {
            settings.ProjectDir = Value(project);
            settings.JediKnightTestExecutable = Value(jkExecutable);
            settings.MysteriesTestExecutable = Value(motsExecutable);
            settings.JediKnightTestEngine = (GameEngineProfile)jkEngine.SelectedIndex;
            settings.MysteriesTestEngine = (GameEngineProfile)motsEngine.SelectedIndex;
            if (command is not null) settings.TestCommand = Value(command);
            settings.Save();
            Close();
        };
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = stack });
        Content = root;

        void AddGame(string name, TextBox executable, ComboBox engine)
        {
            stack.Children.Add(Label(name));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(executable);
            var browse = new Button { Content = "Select EXE…" };
            browse.Click += async (_, _) =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select " + name + " executable", AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("Executable") { Patterns = ["*.exe"] }],
                });
                if (files.FirstOrDefault() is { } file) executable.Text = file.Path.LocalPath;
            };
            row.Children.Add(browse);
            stack.Children.Add(row);
            stack.Children.Add(engine);
        }
    }

    private static TextBox Field(string? text) => new() { Text = text ?? string.Empty, Width = 500 };
    private static string? Value(TextBox field) => string.IsNullOrWhiteSpace(field.Text) ? null : field.Text.Trim();
    private static ComboBox Profile(GameEngineProfile selected) => new()
    {
        ItemsSource = new[] { "Automatic (prefer OpenJKDF2)", "OpenJKDF2", "Retail" },
        SelectedIndex = Enum.IsDefined(selected) ? (int)selected : 0, Width = 300,
        HorizontalAlignment = HorizontalAlignment.Left,
    };
    private static TextBlock Label(string text) => new() { Text = text, FontSize = 12, FontWeight = FontWeight.Bold };
    private static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap,
        FontSize = 11, Foreground = Brushes.Gray };
}
