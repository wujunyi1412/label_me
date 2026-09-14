using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.IO.Path;

namespace LabelMeWpf;

public partial class MainWindow : Window
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private const double PolygonSnapDistancePixels = 8;
    private readonly ObservableCollection<AnnotationShape> _shapes = [];
    private readonly List<string> _imagePaths = [];
    private readonly HashSet<string> _duplicateAnnotationStems = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingAnnotationChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _annotationRefreshTimer;
    private readonly List<Point> _draftPoints = [];
    private string? _folder;
    private string? _annotationFolder;
    private ProjectState _state = new();
    private int _currentIndex = -1;
    private bool _drawing;
    private bool _drawingRectangle;
    private Point? _rectangleStart;
    private Point? _rectanglePreview;
    private bool _loadingSelection;
    private bool _dirty;
    private int _dragShapeIndex = -1;
    private int _dragPointIndex = -1;
    private FileSystemWatcher? _annotationWatcher;

    public MainWindow()
    {
        InitializeComponent();
        ShapeList.ItemsSource = _shapes;
        _annotationRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _annotationRefreshTimer.Tick += AnnotationRefreshTimer_Tick;
        RefreshLabels();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var imageDialog = new OpenFolderDialog { Title = "第 1 步：选择待标注图片目录", Multiselect = false };
        if (imageDialog.ShowDialog(this) != true) return;

        var annotationDialog = new OpenFolderDialog
        {
            Title = "第 2 步：选择 JSON 标注保存目录",
            Multiselect = false,
            InitialDirectory = imageDialog.FolderName
        };
        while (annotationDialog.ShowDialog(this) == true)
        {
            if (!string.Equals(Path.GetFullPath(imageDialog.FolderName).TrimEnd('\\'),
                    Path.GetFullPath(annotationDialog.FolderName).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                if (CanUseAnnotationFolder(imageDialog.FolderName, annotationDialog.FolderName))
                {
                    OpenFolder(imageDialog.FolderName, annotationDialog.FolderName);
                    return;
                }
                continue;
            }
            MessageBox.Show(this, "JSON 保存目录需要与图片目录分开，请重新选择。", "请选择独立目录",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OpenFolder(string folder, string annotationFolder)
    {
        if (!CanLeaveCurrent()) return;
        DisposeAnnotationWatcher();
        CancelDrawing();
        _currentIndex = -1;
        _shapes.Clear();
        MainImage.Source = null;
        FileNameText.Text = "";
        _folder = folder;
        _annotationFolder = annotationFolder;
        AnnotationFolderText.Text = annotationFolder;
        AnnotationFolderText.ToolTip = annotationFolder;
        _imagePaths.Clear();
        try
        {
            _imagePaths.AddRange(Directory.EnumerateFiles(folder)
                .Where(p => ImageExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法读取图片目录：\n{ex.Message}", "打开项目失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "图片目录读取失败";
            return;
        }
        _duplicateAnnotationStems.Clear();
        foreach (var group in _imagePaths.GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
            _duplicateAnnotationStems.Add(group.Key!);

        LoadProjectState();
        _state.ImageFolder = Path.GetFullPath(folder);
        ReconcileProjectState();
        RefreshImageList();
        RefreshLabels();
        UpdateProgress();
        SaveProjectState();
        SetupAnnotationWatcher();
        if (_imagePaths.Count == 0)
        {
            StatusText.Text = "目录中没有支持的图片";
            ImageHost.Width = ImageHost.Height = 0;
            return;
        }
        var index = Math.Clamp(_state.CurrentIndex, 0, _imagePaths.Count - 1);
        SelectImage(index);
    }

    private bool CanUseAnnotationFolder(string imageFolder, string annotationFolder)
    {
        var statePath = Path.Combine(annotationFolder, ".labelme-wpf-progress.json");
        if (!File.Exists(statePath)) return true;
        try
        {
            var state = JsonSerializer.Deserialize<ProjectState>(File.ReadAllText(statePath), JsonOptions);
            if (string.IsNullOrWhiteSpace(state?.ImageFolder) || PathsEqual(state.ImageFolder, imageFolder)) return true;
            MessageBox.Show(this,
                $"这个 JSON 目录已属于另一个图片项目：\n{state.ImageFolder}\n\n请为当前图片目录选择其他 JSON 目录。",
                "JSON 目录已被使用", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        catch
        {
            return true; // 损坏的进度文件会在正式加载时给出明确提示。
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

    private void ReconcileProjectState()
    {
        _state.CompletedImages.Clear();
        foreach (var imagePath in _imagePaths)
        {
            var annotationPath = AnnotationPath(imagePath);
            if (!File.Exists(annotationPath)) continue;
            try
            {
                var data = ReadAnnotation(annotationPath);
                _state.CompletedImages.Add(Path.GetFileName(imagePath));
                foreach (var label in data.Shapes.Select(s => s.Label).Where(s => !string.IsNullOrWhiteSpace(s)))
                {
                    if (!_state.Labels.Any(x => string.Equals(x, label, StringComparison.CurrentCultureIgnoreCase)))
                        _state.Labels.Add(label);
                }
            }
            catch
            {
                // 无效 JSON 不计为已完成；选中该图片时会显示具体读取错误。
            }
        }
    }

    private void SetupAnnotationWatcher()
    {
        if (_annotationFolder == null) return;
        try
        {
            _annotationWatcher = new FileSystemWatcher(_annotationFolder, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            _annotationWatcher.Created += AnnotationFile_Changed;
            _annotationWatcher.Changed += AnnotationFile_Changed;
            _annotationWatcher.Deleted += AnnotationFile_Changed;
            _annotationWatcher.Renamed += AnnotationFile_Renamed;
            _annotationWatcher.Error += AnnotationWatcher_Error;
        }
        catch (Exception ex)
        {
            _annotationWatcher?.Dispose();
            _annotationWatcher = null;
            MessageBox.Show(this, $"无法自动监听 JSON 目录：\n{ex.Message}\n\n仍可使用 F5 手动刷新。",
                "自动刷新不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DisposeAnnotationWatcher()
    {
        _annotationRefreshTimer.Stop();
        _pendingAnnotationChanges.Clear();
        if (_annotationWatcher == null) return;
        _annotationWatcher.EnableRaisingEvents = false;
        _annotationWatcher.Dispose();
        _annotationWatcher = null;
    }

    private void AnnotationFile_Changed(object sender, FileSystemEventArgs e) => QueueAnnotationRefresh(e.FullPath);

    private void AnnotationFile_Renamed(object sender, RenamedEventArgs e)
    {
        QueueAnnotationRefresh(e.OldFullPath);
        QueueAnnotationRefresh(e.FullPath);
    }

    private void AnnotationWatcher_Error(object sender, ErrorEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = "文件监听发生遗漏，正在重新扫描 JSON 目录…";
            RefreshAnnotations(false, true);
        });
    }

    private void QueueAnnotationRefresh(string path)
    {
        if (Path.GetFileName(path).Equals(".labelme-wpf-progress.json", StringComparison.OrdinalIgnoreCase)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_annotationFolder == null || !PathsEqual(Path.GetDirectoryName(path)!, _annotationFolder)) return;
            _pendingAnnotationChanges.Add(path);
            _annotationRefreshTimer.Stop();
            _annotationRefreshTimer.Start();
        });
    }

    private void AnnotationRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _annotationRefreshTimer.Stop();
        var changedPaths = _pendingAnnotationChanges.ToList();
        _pendingAnnotationChanges.Clear();
        var currentChanged = _currentIndex >= 0 &&
                             changedPaths.Any(path => AnnotationChangeAffectsImage(path, _imagePaths[_currentIndex]));
        RefreshAnnotations(false, currentChanged);
    }

    private static bool AnnotationChangeAffectsImage(string changedPath, string imagePath)
    {
        var changedName = Path.GetFileName(changedPath);
        var legacyName = Path.GetFileNameWithoutExtension(imagePath) + ".json";
        var collisionSafeName = Path.GetFileName(imagePath) + ".json";
        return string.Equals(changedName, legacyName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(changedName, collisionSafeName, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshAnnotations(bool manual, bool reloadCurrent)
    {
        if (_folder == null || _annotationFolder == null)
        {
            if (manual) StatusText.Text = "请先打开项目";
            return;
        }

        var hasLocalChanges = _dirty || (_drawing && _draftPoints.Count > 0) ||
                              (_drawingRectangle && _rectangleStart.HasValue);
        if (reloadCurrent && hasLocalChanges)
        {
            var answer = MessageBox.Show(this,
                "当前图片有未保存内容，外部 JSON 已发生变化。是否放弃本地修改并重新加载？",
                "检测到外部修改", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            reloadCurrent = answer == MessageBoxResult.Yes;
        }

        ReconcileProjectState();
        RefreshLabels();
        UpdateProgress();
        SaveProjectState();

        if (reloadCurrent && _currentIndex >= 0)
            LoadCurrentImage();
        else if (manual)
            StatusText.Text = "标注列表和进度已刷新";
    }

    private void LoadProjectState()
    {
        var path = StatePath();
        try
        {
            _state = File.Exists(path)
                ? JsonSerializer.Deserialize<ProjectState>(File.ReadAllText(path), JsonOptions) ?? new()
                : new();
        }
        catch (Exception ex)
        {
            _state = new();
            MessageBox.Show($"进度文件读取失败，将使用新进度。\n{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _state.Labels ??= [];
        if (_state.FormatVersion < 2)
        {
            // 只清除旧版本自动创建的占位类别，不影响用户以后主动添加同名类别。
            _state.Labels.RemoveAll(label => label == "目标");
            _state.FormatVersion = 2;
        }
        _state.CompletedImages ??= [];
    }

    private void SelectImage(int index)
    {
        if (index < 0 || index >= _imagePaths.Count || index == _currentIndex) return;
        if (!CanLeaveCurrent())
        {
            SyncListSelection();
            return;
        }
        _currentIndex = index;
        _state.CurrentIndex = index;
        LoadCurrentImage();
        SaveProjectState();
        SyncListSelection();
    }

    private void SyncListSelection()
    {
        _loadingSelection = true;
        ImageList.SelectedIndex = _currentIndex;
        if (_currentIndex >= 0) ImageList.ScrollIntoView(ImageList.SelectedItem);
        _loadingSelection = false;
    }

    private void LoadCurrentImage()
    {
        CancelDrawing();
        _shapes.Clear();
        var imagePath = _imagePaths[_currentIndex];
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(imagePath);
            bitmap.EndInit();
            bitmap.Freeze();
            MainImage.Source = bitmap;
            ImageHost.Width = bitmap.PixelWidth;
            ImageHost.Height = bitmap.PixelHeight;
            DrawingCanvas.Width = bitmap.PixelWidth;
            DrawingCanvas.Height = bitmap.PixelHeight;
            FileNameText.Text = Path.GetFileName(imagePath);

            var jsonPath = AnnotationPath(imagePath);
            if (File.Exists(jsonPath))
            {
                var data = ReadAnnotation(jsonPath);
                foreach (var shape in data.Shapes) _shapes.Add(shape);
            }
            _dirty = false;
            RenderShapes();
            StatusText.Text = $"第 {_currentIndex + 1} / {_imagePaths.Count} 张 · {_shapes.Count} 个标注";
            Dispatcher.BeginInvoke(FitImage, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"图片或标注读取失败：\n{imagePath}\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool CanLeaveCurrent()
    {
        var hasDraft = (_drawing && _draftPoints.Count > 0) || (_drawingRectangle && _rectangleStart.HasValue);
        if (hasDraft)
        {
            var discard = MessageBox.Show(this, "当前还有未完成的形状，是否放弃该形状并继续？", "标注未完成",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (discard != MessageBoxResult.Yes) return false;
        }
        if (_drawing || _drawingRectangle) CancelDrawing();
        if (!_dirty) return true;
        var result = MessageBox.Show("当前图片有未保存的修改，是否保存？", "未保存", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.Yes) return SaveCurrent();
        _dirty = false;
        return true;
    }

    private bool SaveCurrent()
    {
        if (_currentIndex < 0 || MainImage.Source is not BitmapSource bitmap) return false;
        if (_drawing || _drawingRectangle)
        {
            MessageBox.Show(this, "当前形状还没有完成。请先完成标注，或按 Esc 取消后再保存。", "标注未完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        try
        {
            var imagePath = _imagePaths[_currentIndex];
            var data = new AnnotationFile
            {
                ImagePath = Path.GetRelativePath(_annotationFolder!, imagePath),
                ImageWidth = bitmap.PixelWidth,
                ImageHeight = bitmap.PixelHeight,
                Shapes = _shapes.ToList()
            };
            WriteTextAtomic(AnnotationPath(imagePath), JsonSerializer.Serialize(data, JsonOptions));
            _state.CompletedImages.Add(Path.GetFileName(imagePath));
            _dirty = false;
            if (!SaveProjectState())
            {
                MessageBox.Show(this, "标注 JSON 已保存，但进度文件保存失败。程序不会自动进入下一张。", "进度保存失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            UpdateProgress();
            StatusText.Text = $"已保存 {Path.GetFileName(AnnotationPath(imagePath))} · {_shapes.Count} 个标注";
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败：\n{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private bool SaveProjectState()
    {
        if (_folder == null || _annotationFolder == null) return false;
        try
        {
            WriteTextAtomic(StatePath(), JsonSerializer.Serialize(_state, JsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"进度保存失败：{ex.Message}";
            return false;
        }
    }

    private static void WriteTextAtomic(string path, string content)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, content);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private string StatePath() => Path.Combine(_annotationFolder!, ".labelme-wpf-progress.json");
    private string AnnotationPath(string imagePath)
    {
        var stem = Path.GetFileNameWithoutExtension(imagePath);
        var legacyPath = Path.Combine(_annotationFolder!, stem + ".json");
        if (!_duplicateAnnotationStems.Contains(stem)) return legacyPath;

        var collisionSafePath = Path.Combine(_annotationFolder!, Path.GetFileName(imagePath) + ".json");
        if (File.Exists(collisionSafePath) || !File.Exists(legacyPath)) return collisionSafePath;

        // 若后来才加入同名不同扩展名图片，继续让旧 JSON 对应其原图，避免标注“消失”。
        try
        {
            var oldData = JsonSerializer.Deserialize<AnnotationFile>(File.ReadAllText(legacyPath), JsonOptions);
            if (string.Equals(Path.GetFileName(oldData?.ImagePath), Path.GetFileName(imagePath), StringComparison.OrdinalIgnoreCase))
                return legacyPath;
        }
        catch
        {
            // 无法确认归属时使用不会覆盖旧文件的新名称。
        }
        return collisionSafePath;
    }

    private static AnnotationFile ReadAnnotation(string path)
    {
        var data = JsonSerializer.Deserialize<AnnotationFile>(File.ReadAllText(path), JsonOptions)
                   ?? throw new InvalidDataException("JSON 内容为空。");
        if (data.Shapes == null) throw new InvalidDataException("JSON 缺少 shapes 数组。");
        foreach (var shape in data.Shapes)
        {
            if (shape == null || shape.Points == null || shape.Points.Any(point => point == null || point.Length < 2))
                throw new InvalidDataException("JSON 中存在无效的标注坐标。");
            var minimumPoints = shape.ShapeType == "rectangle" ? 2 : 3;
            if (shape.Points.Count < minimumPoints)
                throw new InvalidDataException($"类别“{shape.Label}”的标注点数量不足。");
            if (string.IsNullOrWhiteSpace(shape.Label))
                throw new InvalidDataException("JSON 中存在空类别名称。");
            shape.ShapeType = shape.ShapeType == "rectangle" ? "rectangle" : "polygon";
        }
        return data;
    }

    private void StartDrawing()
    {
        if (_currentIndex < 0) return;
        CancelDrawing();
        _drawing = true;
        _draftPoints.Clear();
        DrawButton.Content = "正在绘制…靠近起点闭合";
        DrawButton.Background = new SolidColorBrush(Color.FromRgb(176, 91, 36));
        DrawingCanvas.Cursor = Cursors.Cross;
        RenderShapes();
    }

    private void StartRectangle()
    {
        if (_currentIndex < 0) return;
        CancelDrawing();
        _drawingRectangle = true;
        DrawButton.Content = "矩形模式：请单击两个角点…";
        DrawButton.Background = new SolidColorBrush(Color.FromRgb(31, 126, 177));
        DrawingCanvas.Cursor = Cursors.Cross;
    }

    private string? ChooseLabel()
    {
        var dialog = new CategoryDialog(_state.Labels, LabelCombo.SelectedItem as string) { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.SelectedLabel)) return null;
        return AddLabelIfMissing(dialog.SelectedLabel);
    }

    private void FinishDrawing()
    {
        if (!_drawing) return;
        if (_draftPoints.Count >= 3)
        {
            var label = ChooseLabel();
            if (label != null)
            {
                _shapes.Add(new AnnotationShape
                {
                    Label = label,
                    ShapeType = "polygon",
                    Points = _draftPoints.Select(p => new[] { Math.Round(p.X, 2), Math.Round(p.Y, 2) }).ToList()
                });
                _dirty = true;
                ShapeList.SelectedIndex = _shapes.Count - 1;
            }
        }
        CancelDrawing();
    }

    private void CancelDrawing()
    {
        _drawing = false;
        _drawingRectangle = false;
        _rectangleStart = null;
        _rectanglePreview = null;
        _draftPoints.Clear();
        DrawButton.Content = "开始绘制  W";
        DrawButton.Background = new SolidColorBrush(Color.FromRgb(23, 105, 170));
        DrawingCanvas.Cursor = Cursors.Arrow;
        DrawingCanvas.ReleaseMouseCapture();
        RenderShapes();
    }

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DrawingCanvas.Focus();
        Keyboard.Focus(DrawingCanvas);
        var point = ClampPoint(e.GetPosition(DrawingCanvas));
        if (_drawing)
        {
            if (_draftPoints.Count >= 3 && IsNear(point, _draftPoints[0], PolygonSnapDistancePixels))
            {
                FinishDrawing();
                e.Handled = true;
                return;
            }
            _draftPoints.Add(point);
            RenderShapes(point);
            e.Handled = true;
            return;
        }
        if (_drawingRectangle)
        {
            if (!_rectangleStart.HasValue)
            {
                _rectangleStart = _rectanglePreview = point;
                RenderShapes();
            }
            else
            {
                var start = _rectangleStart.Value;
                if (Math.Abs(point.X - start.X) >= 3 && Math.Abs(point.Y - start.Y) >= 3)
                {
                    var label = ChooseLabel();
                    if (label != null)
                    {
                        _shapes.Add(new AnnotationShape
                        {
                            Label = label,
                            ShapeType = "rectangle",
                            Points = [[Math.Round(start.X, 2), Math.Round(start.Y, 2)], [Math.Round(point.X, 2), Math.Round(point.Y, 2)]]
                        });
                        _dirty = true;
                        ShapeList.SelectedIndex = _shapes.Count - 1;
                    }
                    CancelDrawing();
                }
            }
            e.Handled = true;
            return;
        }

        FindNearestVertex(point, out _dragShapeIndex, out _dragPointIndex);
        if (_dragShapeIndex >= 0)
        {
            ShapeList.SelectedIndex = _dragShapeIndex;
            DrawingCanvas.CaptureMouse();
            e.Handled = true;
            return;
        }
        ShapeList.SelectedIndex = HitTestShape(point);
        e.Handled = true;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        var point = ClampPoint(e.GetPosition(DrawingCanvas));
        if (_drawing)
        {
            var preview = _draftPoints.Count >= 3 && IsNear(point, _draftPoints[0], PolygonSnapDistancePixels)
                ? _draftPoints[0]
                : point;
            RenderShapes(preview);
        }
        if (_drawingRectangle && _rectangleStart.HasValue)
        {
            _rectanglePreview = point;
            RenderShapes();
            return;
        }
        if (_dragShapeIndex >= 0 && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateDraggedVertex(point);
            _dirty = true;
            RenderShapes();
        }
        else if (_dragShapeIndex >= 0)
        {
            _dragShapeIndex = _dragPointIndex = -1;
            DrawingCanvas.ReleaseMouseCapture();
        }
    }

    private void Canvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_drawing) { FinishDrawing(); e.Handled = true; }
        else if (_drawingRectangle) { CancelDrawing(); e.Handled = true; }
    }

    private bool IsNear(Point a, Point b, double screenPixels)
    {
        var radius = screenPixels / ImageScale.ScaleX;
        return (a - b).LengthSquared <= radius * radius;
    }

    private Point ClampPoint(Point p) => new(Math.Clamp(p.X, 0, DrawingCanvas.Width), Math.Clamp(p.Y, 0, DrawingCanvas.Height));

    private void FindNearestVertex(Point point, out int shapeIndex, out int pointIndex)
    {
        shapeIndex = pointIndex = -1;
        var radius = 10 / ImageScale.ScaleX;
        var best = radius * radius;
        for (var s = 0; s < _shapes.Count; s++)
        {
        var editablePoints = GetOutlinePoints(_shapes[s]);
        for (var p = 0; p < editablePoints.Count; p++)
        {
            var item = editablePoints[p];
            var distance = Math.Pow(item.X - point.X, 2) + Math.Pow(item.Y - point.Y, 2);
            if (distance <= best) { best = distance; shapeIndex = s; pointIndex = p; }
        }
        }
    }

    private void UpdateDraggedVertex(Point point)
    {
        var shape = _shapes[_dragShapeIndex];
        var x = Math.Round(point.X, 2);
        var y = Math.Round(point.Y, 2);
        if (shape.ShapeType != "rectangle")
        {
            shape.Points[_dragPointIndex] = [x, y];
            return;
        }

        switch (_dragPointIndex)
        {
            case 0: shape.Points[0] = [x, y]; break;
            case 1:
                shape.Points[1][0] = x;
                shape.Points[0][1] = y;
                break;
            case 2: shape.Points[1] = [x, y]; break;
            case 3:
                shape.Points[0][0] = x;
                shape.Points[1][1] = y;
                break;
        }
    }

    private void RenderShapes(Point? preview = null)
    {
        DrawingCanvas.Children.Clear();
        for (var i = 0; i < _shapes.Count; i++)
        {
            var selected = i == ShapeList.SelectedIndex;
            AddPolygon(GetOutlinePoints(_shapes[i]), selected ? Colors.Yellow : LabelColor(_shapes[i].Label), true, selected);
        }
        if (_drawing && _draftPoints.Count > 0)
        {
            var pts = _draftPoints.ToList();
            if (preview.HasValue) pts.Add(preview.Value);
            AddPolygon(pts, Colors.Orange, false, false);
        }
        if (_drawingRectangle && _rectangleStart.HasValue && _rectanglePreview.HasValue)
        {
            AddPolygon(RectanglePoints(_rectangleStart.Value, _rectanglePreview.Value), Colors.DeepSkyBlue, true, false);
        }
    }

    private void AddPolygon(IEnumerable<Point> points, Color color, bool closed, bool selected)
    {
        var list = points.ToList();
        if (list.Count == 0) return;
        var polygon = new Polyline
        {
            Points = new PointCollection(closed ? list.Append(list[0]) : list),
            Stroke = new SolidColorBrush(color), StrokeThickness = (selected ? 4 : 2) / ImageScale.ScaleX,
            Fill = closed ? new SolidColorBrush(Color.FromArgb((byte)(selected ? 75 : 35), color.R, color.G, color.B)) : Brushes.Transparent,
            IsHitTestVisible = false
        };
        DrawingCanvas.Children.Add(polygon);
        for (var i = 0; i < list.Count; i++)
        {
            var dot = new Ellipse { Width = 8 / ImageScale.ScaleX, Height = 8 / ImageScale.ScaleX, Fill = new SolidColorBrush(color), Stroke = Brushes.Black, StrokeThickness = 1 / ImageScale.ScaleX, IsHitTestVisible = false };
            Canvas.SetLeft(dot, list[i].X - dot.Width / 2); Canvas.SetTop(dot, list[i].Y - dot.Height / 2);
            DrawingCanvas.Children.Add(dot);
        }
    }

    private static List<Point> GetOutlinePoints(AnnotationShape shape)
    {
        var points = shape.ToPoints().ToList();
        return shape.ShapeType == "rectangle" && points.Count >= 2
            ? RectanglePoints(points[0], points[1])
            : points;
    }

    private static List<Point> RectanglePoints(Point a, Point b) =>
        [a, new Point(b.X, a.Y), b, new Point(a.X, b.Y)];

    private int HitTestShape(Point point)
    {
        for (var i = _shapes.Count - 1; i >= 0; i--)
        {
            var polygon = GetOutlinePoints(_shapes[i]);
            if (IsPointInsidePolygon(point, polygon) || IsNearPolygonEdge(point, polygon, 7 / ImageScale.ScaleX)) return i;
        }
        return -1;
    }

    private static bool IsNearPolygonEdge(Point point, IReadOnlyList<Point> polygon, double tolerance)
    {
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            var ab = b - a;
            var lengthSquared = ab.LengthSquared;
            var t = lengthSquared == 0 ? 0 : Math.Clamp(Vector.Multiply(point - a, ab) / lengthSquared, 0, 1);
            if ((point - (a + ab * t)).Length <= tolerance) return true;
        }
        return false;
    }

    private static bool IsPointInsidePolygon(Point point, IReadOnlyList<Point> polygon)
    {
        if (polygon.Count < 3) return false;
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private static Color LabelColor(string label)
    {
        var colors = new[] { Colors.Lime, Colors.DeepSkyBlue, Colors.Magenta, Colors.OrangeRed, Colors.Cyan, Colors.Gold, Colors.SpringGreen };
        var hash = label.Aggregate(17, (h, c) => unchecked(h * 31 + c));
        return colors[(hash & int.MaxValue) % colors.Length];
    }

    private void RefreshLabels()
    {
        var selected = LabelCombo.SelectedItem as string;
        LabelCombo.ItemsSource = null;
        LabelCombo.ItemsSource = _state.Labels;
        LabelCombo.SelectedItem = _state.Labels.FirstOrDefault(x => x == selected) ?? _state.Labels.FirstOrDefault();
    }

    private string AddLabelIfMissing(string label)
    {
        var existing = _state.Labels.FirstOrDefault(x => string.Equals(x, label, StringComparison.CurrentCultureIgnoreCase));
        if (existing == null) _state.Labels.Add(label);
        RefreshLabels();
        var canonicalLabel = existing ?? label;
        LabelCombo.SelectedItem = canonicalLabel;
        SaveProjectState();
        return canonicalLabel;
    }

    private void UpdateProgress()
    {
        var names = _imagePaths.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var count = _state.CompletedImages.Count(names.Contains);
        ProgressBar.Maximum = Math.Max(1, _imagePaths.Count);
        ProgressBar.Value = count;
        ProgressText.Text = $"{count} / {_imagePaths.Count} 已完成";
        RefreshImageList();
    }

    private void RefreshImageList()
    {
        var selectedIndex = _currentIndex;
        _loadingSelection = true;
        ImageList.ItemsSource = _imagePaths.Select(p =>
        {
            var name = Path.GetFileName(p);
            return _state.CompletedImages.Contains(name) ? $"✓  {name}" : $"    {name}";
        }).ToList();
        ImageList.SelectedIndex = selectedIndex;
        _loadingSelection = false;
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => SelectImage(_currentIndex - 1);
    private void Next_Click(object sender, RoutedEventArgs e) => SelectImage(_currentIndex + 1);
    private void Save_Click(object sender, RoutedEventArgs e) => SaveCurrent();
    private void SaveNext_Click(object sender, RoutedEventArgs e) { if (SaveCurrent()) SelectImage(_currentIndex + 1); }
    private void Draw_Click(object sender, RoutedEventArgs e) => StartSelectedDrawing();

    private void StartSelectedDrawing()
    {
        if (_drawing) { FinishDrawing(); return; }
        if (_drawingRectangle) { CancelDrawing(); return; }
        if (RectangleModeRadio.IsChecked == true) StartRectangle();
        else StartDrawing();
    }
    private void DrawingMode_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && (_drawing || _drawingRectangle)) CancelDrawing();
    }
    private void FitImage_Click(object sender, RoutedEventArgs e) => FitImage();
    private void RefreshAnnotations_Click(object sender, RoutedEventArgs e) => RefreshAnnotations(true, true);

    private void ImageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingSelection && ImageList.SelectedIndex >= 0) SelectImage(ImageList.SelectedIndex);
    }

    private void ShapeList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RenderShapes();

    private void AddLabel_Click(object sender, RoutedEventArgs e)
    {
        var label = NewLabelTextBox.Text.Trim();
        if (string.IsNullOrEmpty(label)) { NewLabelTextBox.Focus(); return; }
        AddLabelIfMissing(label);
        NewLabelTextBox.Clear();
        if (_currentIndex >= 0)
            Keyboard.Focus(DrawingCanvas);
    }

    private void NewLabelTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddLabel_Click(sender, e);
        e.Handled = true;
    }

    private void RemoveLabel_Click(object sender, RoutedEventArgs e)
    {
        if (LabelCombo.SelectedItem is string label && _state.Labels.Remove(label))
        { RefreshLabels(); SaveProjectState(); }
    }

    private void DeleteShape_Click(object sender, RoutedEventArgs e)
    {
        var index = ShapeList.SelectedIndex;
        if (index < 0) return;
        _shapes.RemoveAt(index); _dirty = true; RenderShapes();
    }

    private void ClearShapes_Click(object sender, RoutedEventArgs e)
    {
        if (_shapes.Count == 0) return;
        if (MessageBox.Show("确定清空当前图片的全部标注？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        { _shapes.Clear(); _dirty = true; RenderShapes(); }
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ImageScale == null) return;
        ImageScale.ScaleX = ImageScale.ScaleY = e.NewValue;
        if (ZoomText != null) ZoomText.Text = $"{e.NewValue:P0}";
        if (DrawingCanvas != null) RenderShapes();
    }

    private void ImageScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || MainImage.Source == null) return;

        var oldZoom = ZoomSlider.Value;
        var wheelSteps = e.Delta / 120.0;
        var newZoom = Math.Clamp(oldZoom * Math.Pow(1.12, wheelSteps), ZoomSlider.Minimum, ZoomSlider.Maximum);
        if (Math.Abs(newZoom - oldZoom) < 0.0001) { e.Handled = true; return; }

        var cursor = e.GetPosition(ImageScroll);
        var oldHorizontalOffset = ImageScroll.HorizontalOffset;
        var oldVerticalOffset = ImageScroll.VerticalOffset;
        ZoomSlider.Value = newZoom;
        ImageScroll.UpdateLayout();

        var ratio = newZoom / oldZoom;
        ImageScroll.ScrollToHorizontalOffset((oldHorizontalOffset + cursor.X) * ratio - cursor.X);
        ImageScroll.ScrollToVerticalOffset((oldVerticalOffset + cursor.Y) * ratio - cursor.Y);
        e.Handled = true;
    }

    private void FitImage()
    {
        if (MainImage.Source is not BitmapSource bitmap) return;
        var width = ImageScroll.ViewportWidth;
        var height = ImageScroll.ViewportHeight;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 1 || height <= 1)
        {
            width = ImageScroll.ActualWidth;
            height = ImageScroll.ActualHeight;
        }
        if (width <= 1 || height <= 1) return;
        ZoomSlider.Value = Math.Clamp(Math.Min((width - 12) / bitmap.PixelWidth, (height - 12) / bitmap.PixelHeight),
            ZoomSlider.Minimum, ZoomSlider.Maximum);
        ImageScroll.ScrollToHorizontalOffset(0);
        ImageScroll.ScrollToVerticalOffset(0);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O) { OpenFolder_Click(sender, e); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { SaveCurrent(); e.Handled = true; return; }
        if (e.Key == Key.F5) { RefreshAnnotations(true, true); e.Handled = true; return; }
        if (e.Key == Key.Escape && (_drawing || _drawingRectangle)) { CancelDrawing(); e.Handled = true; return; }
        if (e.Key == Key.Enter && _drawing) { FinishDrawing(); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox) return;
        if (e.Key == Key.Delete) { DeleteShape_Click(sender, e); e.Handled = true; return; }
        if (e.Key == Key.A) { SelectImage(_currentIndex - 1); e.Handled = true; }
        else if (e.Key == Key.D) { SelectImage(_currentIndex + 1); e.Handled = true; }
        else if (e.Key == Key.W) { StartSelectedDrawing(); e.Handled = true; }
        else if (e.Key == Key.F) { FitImage(); e.Handled = true; }
        else if (e.Key == Key.Space) { if (SaveCurrent()) SelectImage(_currentIndex + 1); e.Handled = true; }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!CanLeaveCurrent()) e.Cancel = true;
        else
        {
            SaveProjectState();
            DisposeAnnotationWatcher();
        }
    }
}
