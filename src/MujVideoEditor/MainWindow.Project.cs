using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using MujVideoEditor.Models;
using MujVideoEditor.Services;

namespace MujVideoEditor;

/// <summary>
/// Nový / Otevřít / Uložit projekt a hlídání neuložených změn.
/// </summary>
public partial class MainWindow
{
    private string? _projectPath;
    private bool _isDirty;
    private bool _isLoadingProject;

    private string ProjectName =>
        _projectPath is null ? "Nový projekt" : Path.GetFileNameWithoutExtension(_projectPath);

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = MenuButton.ContextMenu!;
        menu.PlacementTarget = MenuButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void MarkDirty()
    {
        if (_isLoadingProject || _isDirty)
            return;

        _isDirty = true;
        UpdateProjectTitle();
    }

    private void UpdateProjectTitle()
    {
        ProjectTitle.Text = _isDirty ? $"{ProjectName} *" : ProjectName;
        Title = $"{ProjectName} – Můj video editor";
    }

    private void NewProject_Click(object sender, RoutedEventArgs e) => NewProject();

    private void OpenProject_Click(object sender, RoutedEventArgs e) => OpenProject();

    private void SaveProject_Click(object sender, RoutedEventArgs e) => SaveProject();

    private void SaveProjectAs_Click(object sender, RoutedEventArgs e) => SaveProjectAs();

    private void NewProject()
    {
        if (!ConfirmDiscardChanges())
            return;

        LoadIntoEditor(null, [], new TimelineState([], []), AspectRatio.Landscape);
    }

    private void OpenProject(string? path = null)
    {
        if (!ConfirmDiscardChanges())
            return;

        if (path is null)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Otevřít projekt",
                Filter = ProjectSerializer.DialogFilter,
                InitialDirectory = DefaultProjectFolder()
            };
            if (dialog.ShowDialog(this) != true)
                return;
            path = dialog.FileName;
        }

        ProjectSerializer.LoadedProject project;
        try
        {
            project = ProjectSerializer.Load(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Projekt se nepodařilo otevřít.\n\n{ex.Message}", "Otevřít projekt",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        LoadIntoEditor(path, project.Media, project.Timeline, project.Aspect);

        if (project.MissingFiles.Count > 0)
        {
            MessageBox.Show(this,
                "Některé soubory už nejsou na původním místě, proto chybí i v projektu:\n\n" +
                string.Join("\n", project.MissingFiles.Take(10)) +
                (project.MissingFiles.Count > 10 ? $"\n… a dalších {project.MissingFiles.Count - 10}" : ""),
                "Otevřít projekt", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Vrati true, kdyz se projekt opravdu ulozil.</summary>
    private bool SaveProject() => _projectPath is null ? SaveProjectAs() : SaveTo(_projectPath);

    private bool SaveProjectAs()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Uložit projekt",
            Filter = ProjectSerializer.DialogFilter,
            DefaultExt = ProjectSerializer.Extension,
            FileName = _projectPath is null ? "Muj_projekt" : Path.GetFileName(_projectPath),
            InitialDirectory = _projectPath is null ? DefaultProjectFolder() : Path.GetDirectoryName(_projectPath)
        };

        return dialog.ShowDialog(this) == true && SaveTo(dialog.FileName);
    }

    private bool SaveTo(string path)
    {
        try
        {
            ProjectSerializer.Save(path, _mediaItems, TimelineEditor.Model.Snapshot(), _aspect);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Projekt se nepodařilo uložit.\n\n{ex.Message}", "Uložit projekt",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        _projectPath = path;
        _isDirty = false;
        UpdateProjectTitle();
        if (_mode != PreviewMode.Library)
            ShowProjectDetails();
        return true;
    }

    /// <summary>Kdyz jsou neulozene zmeny, zepta se na ulozeni. Vrati false, kdyz uzivatel akci zrusil.</summary>
    private bool ConfirmDiscardChanges()
    {
        if (!_isDirty)
            return true;

        var answer = MessageBox.Show(this, $"Chceš uložit změny v projektu „{ProjectName}“?", "Můj video editor",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        return answer switch
        {
            MessageBoxResult.Yes => SaveProject(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private void LoadIntoEditor(string? projectPath, List<MediaItem> media, TimelineState timeline, AspectRatio aspect)
    {
        _isLoadingProject = true;
        try
        {
            ClearPreview();
            _mediaItems.Clear();
            _durationTasks.Clear();
            _imageCache.Clear();
            foreach (var item in media)
                _mediaItems.Add(item);

            SetAspect(aspect);
            TimelineEditor.Load(timeline);
            _projectPath = projectPath;
            _isDirty = false;
            UpdateProjectTitle();

            if (!timeline.VideoClips.Concat(timeline.AudioClips).Any())
                ShowProjectDetails();
            else
                EnterTimelineMode();
        }
        finally
        {
            _isLoadingProject = false;
        }

        _ = LoadMediaInfoAsync(media);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!ConfirmDiscardChanges())
            e.Cancel = true;
        base.OnClosing(e);
    }

    private static string DefaultProjectFolder() => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
}
