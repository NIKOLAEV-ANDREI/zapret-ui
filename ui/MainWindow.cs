using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace zapret
{
    public sealed class MainWindow : Window
    {
        private enum PageKind
        {
            Home,
            Strategies,
            Lists,
            Diagnostics,
            Updates,
            Settings
        }

        private sealed class Palette
        {
            public Color Background;
            public Color Sidebar;
            public Color Surface;
            public Color SurfaceAlt;
            public Color Stroke;
            public Color Text;
            public Color Muted;
            public Color Accent;
            public Color AccentSoft;
            public Color Success;
            public Color Warning;
            public Color Danger;
            public Color ButtonText;
        }

        private readonly AppPaths paths;
        private readonly ZapretManager manager;
        private readonly StandardTestRunner testRunner;
        private readonly List<Button> navButtons = new List<Button>();
        private readonly List<Button> actionButtons = new List<Button>();
        private readonly ObservableCollection<TestRow> testRows = new ObservableCollection<TestRow>();
        private readonly string stateFile;
        private readonly string selectedStrategyFile;
        private readonly string sidebarStateFile;
        private const double ExpandedSidebarWidth = 230;
        private const double CollapsedSidebarWidth = 78;

        private Palette theme;
        private bool isDarkTheme;
        private bool sidebarCollapsed;
        private PageKind currentPage = PageKind.Home;

        private ColumnDefinition sidebarColumn;
        private ContentControl contentHost;
        private TextBox logBox;
        private ComboBox strategyBox;
        private string gameFilterMode = "off";
        private TextBlock gameFilterModeLabel;
        private readonly List<Button> gameFilterModeButtons = new List<Button>();
        private ComboBox editorFileBox;
        private TextBox editorBox;
        private TextBox statusBox;
        private TextBlock pageTitle;
        private TextBlock pageSubtitle;
        private TextBlock protectionTitle;
        private TextBlock protectionSubtitle;
        private TextBlock activeStrategyText;
        private TextBlock serviceTileText;
        private TextBlock processTileText;
        private TextBlock ipsetTileText;
        private TextBlock gameTileText;
        private Border protectionRing;
        private Border protectionCore;
        private TextBlock protectionIconText;
        private Button startButton;
        private CheckBox serviceToggle;
        private CheckBox updateCheckToggle;
        private bool suppressToggleEvents;
        private Forms.NotifyIcon trayIcon;
        private ListBox testStrategyList;
        private ComboBox testScopeBox;
        private DataGrid testGrid;
        private ProgressBar testProgressBar;
        private TextBlock testProgressText;
        private TextBlock bestConfigText;
        private StackPanel testSummaryPanel;
        private Button testStartButton;
        private Button testCancelButton;
        private volatile bool cancelTests;
        private bool testsRunning;
        private bool updateCheckStarted;
        private bool bypassCloseConfirmation;
        private List<StrategySummary> recommendedStrategies = new List<StrategySummary>();

        public MainWindow()
        {
            paths = new AppPaths();
            manager = new ZapretManager(paths);
            testRunner = new StandardTestRunner(paths, manager);
            stateFile = System.IO.Path.Combine(paths.Utils, "ui_last_tests.tsv");
            selectedStrategyFile = System.IO.Path.Combine(paths.Utils, "ui_selected_strategy.txt");
            sidebarStateFile = System.IO.Path.Combine(paths.Utils, "ui_sidebar.collapsed");
            LoadPersistedState();
            sidebarCollapsed = IsSidebarCollapsedSaved();
            isDarkTheme = manager.IsDarkThemeEnabled();
            theme = isDarkTheme ? DarkPalette() : LightPalette();

            Title = "Zapret";
            Width = 1180;
            Height = 790;
            MinWidth = Width;
            MinHeight = Height;
            MaxWidth = Width;
            MaxHeight = Height;
            ResizeMode = ResizeMode.CanMinimize;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                CornerRadius = new CornerRadius(0),
                GlassFrameThickness = new Thickness(0),
                ResizeBorderThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            });
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            BuildUi();
            InitializeTray();
            Navigate(PageKind.Home);
            RefreshStatus();
            ShowPendingAppUpdateNotice();
            BeginAutoUpdateCheck();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (bypassCloseConfirmation)
            {
                DisposeTray();
                base.OnClosing(e);
                return;
            }

            var runtime = manager.GetRuntimeStatus();
            var active = runtime.WinwsRunning || runtime.ZapretServiceRunning;

            var message = active
                ? "Закрытие окна остановит запущенный конфиг. Продолжить?"
                : "Закрыть приложение?";

            if (!ShowCloseConfirmation(message, active))
            {
                e.Cancel = true;
                return;
            }

            if (active)
            {
                manager.StopProtection();
            }

            DisposeTray();

            base.OnClosing(e);
        }

        private void DisposeTray()
        {
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }
        }

        private void InitializeTray()
        {
            trayIcon = new Forms.NotifyIcon();
            trayIcon.Text = "Zapret";
            trayIcon.Icon = Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location);
            trayIcon.Visible = true;
            trayIcon.DoubleClick += delegate
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            };

            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Открыть", null, delegate
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            });
            menu.Items.Add("Выход", null, delegate
            {
                Close();
            });
            trayIcon.ContextMenuStrip = menu;
        }

        private bool ShowCloseConfirmation(string message, bool active)
        {
            var dialog = new Window
            {
                Title = "Zapret",
                Owner = this,
                Width = 380,
                Height = 190,
                MinWidth = 380,
                MaxWidth = 380,
                MinHeight = 190,
                MaxHeight = 190,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Brushes.Transparent,
                FontFamily = FontFamily,
                FontSize = FontSize,
                ShowInTaskbar = false
            };

            var root = new Border
            {
                Background = Brush(theme.Surface),
                BorderBrush = Brush(theme.Stroke),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(18)
            };
            root.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.ChangedButton != MouseButton.Left) return;
                try { dialog.DragMove(); } catch { }
            };
            dialog.Content = root;

            var shell = new Grid();
            root.Child = shell;

            var dock = new DockPanel { LastChildFill = true };
            shell.Children.Add(dock);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            DockPanel.SetDock(actions, Dock.Bottom);
            dock.Children.Add(actions);

            var cancel = DialogButton("Отмена", delegate
            {
                dialog.DialogResult = false;
                dialog.Close();
            }, false);
            var confirm = DialogButton(active ? "Остановить и выйти" : "Закрыть", delegate
            {
                dialog.DialogResult = true;
                dialog.Close();
            }, true);
            actions.Children.Add(cancel);
            actions.Children.Add(confirm);
            ApplyHorizontalSpacing(actions, 10);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            body.ColumnDefinitions.Add(new ColumnDefinition());
            dock.Children.Add(body);

            var badge = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(18),
                Background = Brush(active ? theme.AccentSoft : theme.SurfaceAlt),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            var icon = new TextBlock
            {
                Text = active ? "!" : "↪",
                Foreground = Brush(active ? theme.Warning : theme.Muted),
                FontSize = active ? 20 : 18,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            badge.Child = icon;
            body.Children.Add(badge);

            var textStack = new StackPanel();
            Grid.SetColumn(textStack, 1);
            body.Children.Add(textStack);
            textStack.Children.Add(new TextBlock
            {
                Text = active ? "Остановить конфиг и выйти?" : "Закрыть Zapret?",
                Foreground = Brush(theme.Text),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            textStack.Children.Add(new TextBlock
            {
                Text = active ? "Запущенный конфиг будет остановлен." : "Приложение будет закрыто.",
                Foreground = Brush(theme.Muted),
                Margin = new Thickness(0, 7, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            return dialog.ShowDialog() == true;
        }

        private bool ShowConfirmationDialog(string title, string message, string confirmText, bool warning)
        {
            var dialog = new Window
            {
                Title = "Zapret",
                Owner = this,
                Width = 420,
                Height = 210,
                MinWidth = 420,
                MaxWidth = 420,
                MinHeight = 210,
                MaxHeight = 210,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Brushes.Transparent,
                FontFamily = FontFamily,
                FontSize = FontSize,
                ShowInTaskbar = false
            };

            var root = new Border
            {
                Background = Brush(theme.Surface),
                BorderBrush = Brush(theme.Stroke),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(18)
            };
            root.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.ChangedButton != MouseButton.Left) return;
                try { dialog.DragMove(); } catch { }
            };
            dialog.Content = root;

            var shell = new Grid();
            root.Child = shell;

            var dock = new DockPanel { LastChildFill = true };
            shell.Children.Add(dock);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            DockPanel.SetDock(actions, Dock.Bottom);
            dock.Children.Add(actions);

            var cancel = DialogButton("Отмена", delegate
            {
                dialog.DialogResult = false;
                dialog.Close();
            }, false);
            var confirm = DialogButton(confirmText, delegate
            {
                dialog.DialogResult = true;
                dialog.Close();
            }, true);
            actions.Children.Add(cancel);
            actions.Children.Add(confirm);
            ApplyHorizontalSpacing(actions, 10);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            body.ColumnDefinitions.Add(new ColumnDefinition());
            dock.Children.Add(body);

            var badge = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(18),
                Background = Brush(warning ? theme.AccentSoft : theme.SurfaceAlt),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            badge.Child = new TextBlock
            {
                Text = warning ? "!" : "i",
                Foreground = Brush(warning ? theme.Warning : theme.Accent),
                FontSize = 19,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            body.Children.Add(badge);

            var textStack = new StackPanel();
            Grid.SetColumn(textStack, 1);
            body.Children.Add(textStack);
            textStack.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = Brush(theme.Text),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            textStack.Children.Add(new TextBlock
            {
                Text = message,
                Foreground = Brush(theme.Muted),
                Margin = new Thickness(0, 7, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            return dialog.ShowDialog() == true;
        }

        private void ShowInfoDialog(string title, string message)
        {
            var dialog = new Window
            {
                Title = "Zapret",
                Owner = this,
                Width = 420,
                Height = 200,
                MinWidth = 420,
                MaxWidth = 420,
                MinHeight = 200,
                MaxHeight = 200,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Brushes.Transparent,
                FontFamily = FontFamily,
                FontSize = FontSize,
                ShowInTaskbar = false
            };

            var root = new Border
            {
                Background = Brush(theme.Surface),
                BorderBrush = Brush(theme.Stroke),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(18)
            };
            root.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.ChangedButton != MouseButton.Left) return;
                try { dialog.DragMove(); } catch { }
            };
            dialog.Content = root;

            var dock = new DockPanel { LastChildFill = true };
            root.Child = dock;

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            DockPanel.SetDock(actions, Dock.Bottom);
            dock.Children.Add(actions);

            actions.Children.Add(DialogButton("Понятно", delegate
            {
                dialog.DialogResult = true;
                dialog.Close();
            }, true));

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            body.ColumnDefinitions.Add(new ColumnDefinition());
            dock.Children.Add(body);

            var badge = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(18),
                Background = Brush(theme.SurfaceAlt),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            badge.Child = new TextBlock
            {
                Text = "i",
                Foreground = Brush(theme.Accent),
                FontSize = 19,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            body.Children.Add(badge);

            var textStack = new StackPanel();
            Grid.SetColumn(textStack, 1);
            body.Children.Add(textStack);
            textStack.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = Brush(theme.Text),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            textStack.Children.Add(new TextBlock
            {
                Text = message,
                Foreground = Brush(theme.Muted),
                Margin = new Thickness(0, 7, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            dialog.ShowDialog();
        }

        private void BuildUi()
        {
            navButtons.Clear();
            actionButtons.Clear();
            Resources[typeof(ScrollBar)] = ScrollBarStyle();

            var root = new Grid();
            root.Background = Brush(theme.Background);
            root.SizeChanged += delegate
            {
                root.Clip = new RectangleGeometry(new Rect(0, 0, root.ActualWidth, root.ActualHeight), 14, 14);
            };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
            root.RowDefinitions.Add(new RowDefinition());

            root.Children.Add(BuildTitleBar());

            var layout = new Grid();
            sidebarColumn = new ColumnDefinition { Width = new GridLength(sidebarCollapsed ? CollapsedSidebarWidth : ExpandedSidebarWidth) };
            layout.ColumnDefinitions.Add(sidebarColumn);
            layout.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetRow(layout, 1);
            root.Children.Add(layout);

            layout.Children.Add(BuildSidebar());

            var main = new DockPanel();
            main.Margin = new Thickness(22, 18, 22, 18);
            Grid.SetColumn(main, 1);
            layout.Children.Add(main);

            main.Children.Add(BuildHeader());

            logBox = new TextBox();
            logBox.Height = 72;
            logBox.IsReadOnly = true;
            logBox.TextWrapping = TextWrapping.Wrap;
            logBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            logBox.Margin = new Thickness(0, 10, 0, 0);
            logBox.Visibility = Visibility.Collapsed;
            StyleTextBox(logBox, true);
            DockPanel.SetDock(logBox, Dock.Bottom);
            main.Children.Add(logBox);

            contentHost = new ContentControl();
            DockPanel.SetDock(contentHost, Dock.Top);
            main.Children.Add(contentHost);

            var frame = new Border
            {
                Background = Brush(theme.Background),
                BorderBrush = Brush(theme.Stroke),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Child = root
            };

            Content = frame;
        }

        private UIElement BuildTitleBar()
        {
            var bar = new Border();
            bar.Background = Brush(theme.Background);
            bar.BorderBrush = Brush(theme.Stroke);
            bar.BorderThickness = new Thickness(0, 0, 0, 1);
            bar.MouseLeftButtonDown += OnTitleBarMouseDown;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.Child = grid;

            var title = new TextBlock
            {
                Text = "Zapret",
                Foreground = Brush(theme.Muted),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0)
            };
            Grid.SetColumn(title, 0);
            grid.Children.Add(title);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);

            buttons.Children.Add(TitleBarButton("−", "Свернуть", delegate { WindowState = WindowState.Minimized; }, false));
            buttons.Children.Add(TitleBarButton("×", "Закрыть", delegate { Close(); }, true));

            return bar;
        }

        private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (e.ClickCount > 1) return;

            try
            {
                DragMove();
            }
            catch
            {
            }
        }

        private UIElement BuildSidebar()
        {
            var side = new Border();
            side.Background = Brush(theme.Sidebar);
            side.Padding = sidebarCollapsed ? new Thickness(12, 22, 12, 18) : new Thickness(18, 22, 18, 18);
            side.BorderBrush = Brush(theme.Stroke);
            side.BorderThickness = new Thickness(0, 0, 1, 0);

            var dock = new DockPanel { LastChildFill = false };
            side.Child = dock;

            var stack = new StackPanel();
            DockPanel.SetDock(stack, Dock.Top);
            dock.Children.Add(stack);

            var brand = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 24),
                Visibility = sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible
            };
            brand.Children.Add(AppIconImage(46));
            brand.Children.Add(new TextBlock
            {
                Text = "Zapret",
                FontSize = 24,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(theme.Text),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            });
            stack.Children.Add(brand);

            if (sidebarCollapsed)
            {
                var icon = AppIconImage(54);
                icon.HorizontalAlignment = HorizontalAlignment.Center;
                icon.Margin = new Thickness(0, 0, 0, 24);
                stack.Children.Add(icon);
            }

            stack.Children.Add(NavButton("\uE80F", "Главная", PageKind.Home));
            stack.Children.Add(NavButton("\uE71D", "Стратегии", PageKind.Strategies));
            stack.Children.Add(NavButton("\uE8FD", "Списки", PageKind.Lists));
            stack.Children.Add(NavButton("\uE721", "Диагностика", PageKind.Diagnostics));
            stack.Children.Add(NavButton("\uE713", "Настройки", PageKind.Settings));

            var bottom = new StackPanel();
            DockPanel.SetDock(bottom, Dock.Bottom);
            dock.Children.Add(bottom);

            var collapse = new Button();
            collapse.Content = sidebarCollapsed ? "›" : "‹";
            collapse.ToolTip = sidebarCollapsed ? "Показать названия вкладок" : "Скрыть названия вкладок";
            collapse.Width = sidebarCollapsed ? 42 : double.NaN;
            collapse.Height = 40;
            collapse.HorizontalAlignment = sidebarCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            collapse.FontSize = 22;
            collapse.FontWeight = FontWeights.SemiBold;
            collapse.Cursor = Cursors.Hand;
            collapse.Style = ButtonStyle(Brush(theme.SurfaceAlt), Brush(theme.AccentSoft), Brush(theme.Text), new CornerRadius(12), true);
            collapse.Click += OnSidebarToggleClick;
            bottom.Children.Add(collapse);

            return side;
        }

        private Image AppIconImage(double size)
        {
            var image = new Image
            {
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

            try
            {
                var assetPath = System.IO.Path.Combine(paths.Root, "ui", "assets", "zapret-icon.png");
                if (!File.Exists(assetPath)) assetPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", "assets", "zapret-icon.png");
                if (!File.Exists(assetPath)) assetPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "zapret-icon.png");

                var source = new BitmapImage();
                source.BeginInit();
                source.CacheOption = BitmapCacheOption.OnLoad;
                source.UriSource = new Uri(assetPath, UriKind.Absolute);
                source.DecodePixelWidth = (int)Math.Round(size * 3);
                source.EndInit();
                source.Freeze();
                image.Source = source;
            }
            catch
            {
            }

            return image;
        }

        private UIElement BuildHeader()
        {
            var grid = new Grid();
            grid.Margin = new Thickness(0, 0, 0, 18);
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            DockPanel.SetDock(grid, Dock.Top);

            var titleStack = new StackPanel();
            pageTitle = new TextBlock
            {
                Text = "Главная",
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(theme.Text)
            };
            pageSubtitle = new TextBlock
            {
                Text = "Управление стратегией, службой и фильтрами",
                Foreground = Brush(theme.Muted),
                Margin = new Thickness(0, 3, 0, 0)
            };
            titleStack.Children.Add(pageTitle);
            titleStack.Children.Add(pageSubtitle);
            Grid.SetColumn(titleStack, 0);
            grid.Children.Add(titleStack);

            var refresh = IconButton("⟳", "Обновить статус", OnRefreshClick);
            refresh.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(refresh, 1);
            grid.Children.Add(refresh);

            return grid;
        }

        private void Navigate(PageKind page)
        {
            currentPage = page;
            UpdateNavSelection();

            if (page == PageKind.Home)
            {
                SetHeader("Главная", "Быстрый запуск и текущее состояние конфига");
                contentHost.Content = BuildHomePage();
            }
            else if (page == PageKind.Strategies)
            {
                SetHeader("Стратегии", "Профили запуска из файлов general*.bat");
                contentHost.Content = BuildStrategiesPage();
            }
            else if (page == PageKind.Lists)
            {
                SetHeader("Списки", "Редактор пользовательских доменов, IP и целей тестов");
                contentHost.Content = BuildListsPage();
            }
            else if (page == PageKind.Diagnostics)
            {
                SetHeader("Диагностика", "Проверка конфликтов и запуск тестов");
                contentHost.Content = BuildDiagnosticsPage();
            }
            else if (page == PageKind.Updates)
            {
                SetHeader("Обновления", "IPSet, hosts и проверка новой версии");
                contentHost.Content = BuildUpdatesPage();
            }
            else
            {
                SetHeader("Настройки", "Тема, ярлык и параметры приложения");
                contentHost.Content = BuildSettingsPage();
            }

            RefreshStatus();
        }

        private UIElement BuildHomePage()
        {
            statusBox = null;

            var root = new StackPanel();

            var hero = Card();
            hero.Padding = new Thickness(24);
            hero.Margin = new Thickness(0, 0, 0, 14);
            var heroGrid = new Grid();
            heroGrid.ColumnDefinitions.Add(new ColumnDefinition());
            heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            hero.Child = heroGrid;

            var heroText = new StackPanel();
            protectionTitle = new TextBlock
            {
                Text = "Конфиг остановлен",
                FontSize = 28,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(theme.Text),
                Margin = new Thickness(0, 0, 0, 6)
            };
            protectionSubtitle = new TextBlock
            {
                Text = "Выберите стратегию и нажмите запуск.",
                Foreground = Brush(theme.Muted),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 18, 18)
            };
            heroText.Children.Add(protectionTitle);
            heroText.Children.Add(protectionSubtitle);
            heroText.Children.Add(new TextBlock
            {
                Text = "Стратегия",
                Foreground = Brush(theme.Muted),
                FontWeight = FontWeights.SemiBold
            });

            strategyBox = new ComboBox();
            strategyBox.MinHeight = 38;
            strategyBox.MaxWidth = 460;
            strategyBox.HorizontalAlignment = HorizontalAlignment.Left;
            strategyBox.Margin = new Thickness(0, 6, 18, 16);
            StyleCombo(strategyBox);
            LoadStrategies();
            strategyBox.SelectionChanged += OnStrategySelectionChanged;
            heroText.Children.Add(strategyBox);

            activeStrategyText = new TextBlock
            {
                Text = "Конфиг остановлен",
                Foreground = Brush(theme.Muted),
                TextWrapping = TextWrapping.Wrap
            };
            heroText.Children.Add(activeStrategyText);

            Grid.SetColumn(heroText, 0);
            heroGrid.Children.Add(heroText);

            var ringWrap = new Grid();
            protectionRing = new Border
            {
                Width = 174,
                Height = 174,
                CornerRadius = new CornerRadius(87),
                BorderThickness = new Thickness(2),
                BorderBrush = Brush(theme.Stroke),
                Background = Brush(theme.SurfaceAlt),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            protectionCore = new Border
            {
                Width = 128,
                Height = 128,
                CornerRadius = new CornerRadius(64),
                BorderThickness = new Thickness(7),
                BorderBrush = Brush(theme.Stroke),
                Background = Brush(theme.Surface),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var innerGlow = new Ellipse
            {
                Width = 82,
                Height = 82,
                Fill = Brush(theme.SurfaceAlt),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.9
            };
            protectionIconText = new TextBlock
            {
                Text = "—",
                FontSize = 48,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(theme.Muted),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            ringWrap.Children.Add(protectionRing);
            ringWrap.Children.Add(protectionCore);
            ringWrap.Children.Add(innerGlow);
            ringWrap.Children.Add(protectionIconText);
            Grid.SetColumn(ringWrap, 1);
            heroGrid.Children.Add(ringWrap);

            root.Children.Add(hero);

            var actions = Card();
            actions.Padding = new Thickness(20);
            actions.Margin = new Thickness(0, 0, 0, 14);
            var actionStack = new StackPanel();
            actions.Child = actionStack;

            startButton = PowerButton("Запустить конфиг", OnPowerClick);
            actionStack.Children.Add(startButton);
            root.Children.Add(actions);

            var tiles = new UniformGrid { Columns = 4 };
            tiles.Children.Add(StatusTile("Служба zapret", "Показывает состояние службы zapret. Если служба установлена и запущена, конфиг может работать без ручного запуска из приложения.", out serviceTileText));
            tiles.Children.Add(StatusTile("winws.exe", "Показывает, запущен ли процесс winws.exe. Это основной процесс, который применяет выбранную стратегию.", out processTileText));
            tiles.Children.Add(StatusTile("IPSet", "Показывает режим IPSet. IPSet нужен стратегиям, которые используют списки IP-диапазонов.", out ipsetTileText));
            tiles.Children.Add(StatusTile("Game Filter", "Показывает режим Game Filter. Он расширяет обработку трафика на игровые TCP/UDP-порты и применяется после перезапуска конфига.", out gameTileText));
            ApplyUniformGridSpacing(tiles, 10);
            root.Children.Add(tiles);

            var recommendations = BuildRecommendedStrategiesPanel();
            if (recommendations != null)
            {
                root.Children.Add(recommendations);
            }

            return Scroll(root);
        }

        private UIElement BuildStrategiesPage()
        {
            var panel = new StackPanel();
            panel.Children.Add(SectionTitle("Доступные стратегии"));
            panel.Children.Add(MutedText("ALT5 помечен как не рекомендуемый в исходном bat-файле. Это предупреждение сохранено."));

            panel.Children.Add(MutedText("Почему ALT5 не рекомендуется: он агрессивнее остальных, использует syndata/multidisorder, меньше ограничивает фильтрацию и может сильнее задевать обычный трафик. Используй его только если остальные стратегии не помогают."));

            var list = new ListBox();
            list.MinHeight = 440;
            list.Margin = new Thickness(0, 14, 0, 0);
            StyleListBox(list);
            list.ItemsSource = manager.GetStrategies().Select(x =>
                x.Name + "    блоков: " + x.Blocks + (x.NotRecommended ? "    не рекомендуется" : ""));
            panel.Children.Add(list);
            return Scroll(panel);
        }

        private UIElement BuildListsPage()
        {
            var panel = new StackPanel();
            panel.Children.Add(SectionTitle("Редактор списков"));

            editorFileBox = new ComboBox { MinHeight = 38, Margin = new Thickness(0, 10, 0, 12) };
            StyleCombo(editorFileBox);
            editorFileBox.Items.Add(new ComboItem("lists\\list-general-user.txt", "Добавить домены"));
            editorFileBox.Items.Add(new ComboItem("lists\\list-exclude-user.txt", "Исключить домены"));
            editorFileBox.Items.Add(new ComboItem("lists\\ipset-exclude-user.txt", "Исключить IP/CIDR"));
            editorFileBox.Items.Add(new ComboItem("utils\\targets.txt", "Цели тестов"));
            editorFileBox.SelectedIndex = 0;
            editorFileBox.SelectionChanged += delegate { LoadEditorFile(); };
            panel.Children.Add(editorFileBox);

            editorBox = new TextBox
            {
                AcceptsReturn = true,
                AcceptsTab = true,
                Height = 390,
                FontFamily = new FontFamily("Consolas"),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                TextWrapping = TextWrapping.NoWrap
            };
            StyleTextBox(editorBox, false);
            panel.Children.Add(editorBox);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            row.Children.Add(ActionButton("Загрузить", delegate { LoadEditorFile(); }, false));
            row.Children.Add(ActionButton("Сохранить", OnSaveEditorClick, true));
            ApplyHorizontalSpacing(row, 10);
            panel.Children.Add(row);

            LoadEditorFile();
            return Scroll(panel);
        }

        private UIElement BuildDiagnosticsPage()
        {
            var panel = new StackPanel();
            panel.Children.Add(SectionTitle("Тесты стратегий"));
            panel.Children.Add(MutedText("Обычный тест запускает выбранную стратегию, проверяет цели из utils\\targets.txt через HTTP/TLS/Ping и переходит к следующей стратегии."));

            var detail = Card();
            detail.Padding = new Thickness(18);
            detail.Margin = new Thickness(0, 14, 0, 14);
            var detailStack = new StackPanel();
            detail.Child = detailStack;
            detailStack.Children.Add(SectionTitle("Подробный статус"));
            statusBox = new TextBox
            {
                Height = 150,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            StyleTextBox(statusBox, true);
            detailStack.Children.Add(statusBox);
            panel.Children.Add(detail);

            var settings = Card();
            settings.Padding = new Thickness(18);
            settings.Margin = new Thickness(0, 14, 0, 14);
            var settingsGrid = new Grid();
            settingsGrid.ColumnDefinitions.Add(new ColumnDefinition());
            settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            settings.Child = settingsGrid;

            var left = new StackPanel();
            left.Children.Add(MutedText("Выбор стратегий"));
            testStrategyList = new ListBox
            {
                MinHeight = 150,
                MaxHeight = 190,
                SelectionMode = SelectionMode.Extended
            };
            StyleListBox(testStrategyList);
            testStrategyList.ItemsSource = manager.GetStrategies();
            left.Children.Add(testStrategyList);
            Grid.SetColumn(left, 0);
            settingsGrid.Children.Add(left);

            var right = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
            right.Children.Add(MutedText("Режим"));
            testScopeBox = new ComboBox { MinHeight = 38, Margin = new Thickness(0, 6, 0, 12) };
            StyleCombo(testScopeBox);
            testScopeBox.Items.Add(new ComboItem("all", "Все стратегии"));
            testScopeBox.Items.Add(new ComboItem("selected", "Только выбранные"));
            testScopeBox.SelectedIndex = 0;
            right.Children.Add(testScopeBox);

            testStartButton = ActionButton("Запустить тест", OnRunInAppTestsClick, true);
            right.Children.Add(testStartButton);

            testCancelButton = ActionButton("Отменить", OnCancelTestsClick, false);
            testCancelButton.IsEnabled = false;
            right.Children.Add(testCancelButton);

            right.Children.Add(ActionButton("Сохранить результат", OnSaveTestResultsClick, false));
            ApplyVerticalSpacing(right, 10);
            Grid.SetColumn(right, 1);
            settingsGrid.Children.Add(right);

            panel.Children.Add(settings);

            var progressCard = Card();
            progressCard.Padding = new Thickness(18);
            progressCard.Margin = new Thickness(0, 0, 0, 14);
            var progressStack = new StackPanel();
            progressCard.Child = progressStack;
            testProgressText = MutedText("Тест не запущен.");
            testProgressBar = new ProgressBar
            {
                Height = 10,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Foreground = Brush(theme.Accent),
                Background = Brush(theme.SurfaceAlt)
            };
            progressStack.Children.Add(testProgressText);
            bestConfigText = new TextBlock
            {
                Text = "Лучшие конфиги появятся после завершения теста.",
                Foreground = Brush(theme.Muted),
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            progressStack.Children.Add(bestConfigText);
            progressStack.Children.Add(testProgressBar);
            panel.Children.Add(progressCard);

            testSummaryPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            panel.Children.Add(testSummaryPanel);

            testGrid = new DataGrid
            {
                Height = 360,
                MinWidth = 760,
                AutoGenerateColumns = false,
                IsReadOnly = true,
                ItemsSource = testRows,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                CanUserResizeColumns = true,
                CanUserSortColumns = false,
                RowHeaderWidth = 0
            };
            StyleDataGrid(testGrid);
            testGrid.Columns.Add(TestColumn("Цель", "Target", 220));
            var testView = CollectionViewSource.GetDefaultView(testRows);
            testView.GroupDescriptions.Clear();
            testView.GroupDescriptions.Add(new PropertyGroupDescription("Strategy"));
            testGrid.ItemsSource = testView;
            testGrid.GroupStyle.Add(CreateStrategyGroupStyle());
            testGrid.Columns.Add(TestColumn("HTTP", "Http", 85));
            testGrid.Columns.Add(TestColumn("TLS 1.2", "Tls12", 95));
            testGrid.Columns.Add(TestColumn("TLS 1.3", "Tls13", 95));
            testGrid.Columns.Add(TestColumn("Ping", "Ping", 120));
            testGrid.Columns.Add(TestColumn("Статус", "Status", 100));

            var gridCard = Card();
            gridCard.Padding = new Thickness(10);
            gridCard.Child = testGrid;
            panel.Children.Add(gridCard);

            return Scroll(panel);
        }

        private UIElement BuildUpdatesPage()
        {
            var panel = new StackPanel();
            panel.Children.Add(SectionTitle("Обновления"));
            panel.Children.Add(MutedText("Все второстепенные функции обслуживания перенесены в настройки, чтобы главный интерфейс оставался простым."));
            return Scroll(panel);
        }

        private UIElement BuildSettingsPage()
        {
            var panel = new StackPanel();
            panel.Children.Add(SectionTitle("Настройки"));

            serviceToggle = SettingsToggle("Запускать приложение вместе с Windows", "Открывает Zapret при входе в Windows. Конфиг сам по себе не запускается: его нужно включить в приложении.", manager.IsAppStartupEnabled(), OnServiceToggleChanged);
            panel.Children.Add(serviceToggle);

            updateCheckToggle = SettingsToggle("Авто-проверка обновлений", "При запуске проверяет наличие новой версии приложения и zapret.", manager.IsUpdateCheckEnabled(), OnUpdateCheckToggleChanged);
            panel.Children.Add(updateCheckToggle);

            panel.Children.Add(SettingsToggle("Темная тема", "Меняет оформление приложения. На работу zapret это не влияет.", isDarkTheme, OnThemeToggleChanged));

            panel.Children.Add(SettingsBlock("Обновление приложения", "Проверяет новую версию интерфейса Zapret, скачивает ее и устанавливает после перезапуска приложения.", BuildAppUpdateActions()));
            panel.Children.Add(SettingsBlock("Обновление zapret", "Проверяет новую версию, предлагает скачать zip-релиз, сохраняет backup и позволяет откатиться.", BuildZapretUpdateActions()));
            panel.Children.Add(SettingsBlock("Game Filter", "Расширяет обработку трафика на игровые TCP/UDP-порты. Применяется после перезапуска конфига.", BuildGameFilterSettings()));
            panel.Children.Add(SettingsBlock("IPSet", "Режим списка IP-диапазонов для стратегий. Обычно лучше оставить loaded.", BuildIpsetSettings()));

            var actions = SettingsBlock("Сервисные действия", "Разовые операции обслуживания списков, hosts и ярлыка.", BuildMaintenanceActions());
            panel.Children.Add(actions);

            panel.Children.Add(SettingsBlock("Обратная связь", "Открывает Telegram-бота, куда пользователи могут отправить ошибки, баги и предложения.", BuildFeedbackSettings()));

            panel.Children.Add(SettingsBlock("О приложении", "Информация о сборке, авторе интерфейса и исходном проекте.", BuildAboutAppSettings()));

            panel.Children.Add(MutedText("Приложение запускается от администратора: без этого службы Windows, WinDivert, netsh и реестр будут работать нестабильно."));
            return Scroll(panel);
        }

        private void RefreshStatus()
        {
            var runtime = manager.GetRuntimeStatus();
            var active = runtime.WinwsRunning || runtime.ZapretServiceRunning;

            if (statusBox != null)
            {
                statusBox.Text = manager.GetStatusText();
            }

            if (protectionTitle != null)
            {
                protectionTitle.Text = active ? "Конфиг запущен" : "Конфиг остановлен";
                protectionTitle.Foreground = Brush(active ? theme.Success : theme.Text);
            }

            if (protectionSubtitle != null)
            {
                protectionSubtitle.Text = active
                    ? "Стратегия запущена. Процесс или служба сейчас работают."
                    : "Выберите стратегию и запустите конфиг.";
            }

            if (protectionRing != null)
            {
                protectionRing.BorderBrush = Brush(active ? theme.AccentSoft : theme.Stroke);
                protectionRing.Background = Brush(theme.SurfaceAlt);
            }

            if (protectionCore != null)
            {
                protectionCore.BorderBrush = Brush(active ? theme.Success : theme.Stroke);
                protectionCore.Background = Brush(active ? theme.Surface : theme.SurfaceAlt);
            }

            if (protectionIconText != null)
            {
                protectionIconText.Text = active ? "✓" : "—";
                protectionIconText.Foreground = Brush(active ? theme.Success : theme.Muted);
                protectionIconText.FontSize = active ? 48 : 46;
            }

            if (activeStrategyText != null)
            {
                if (runtime.ZapretServiceRunning && !string.IsNullOrWhiteSpace(runtime.InstalledStrategyName))
                {
                    activeStrategyText.Text = "Автозапуск Windows: " + runtime.InstalledStrategyName;
                }
                else if (runtime.WinwsRunning)
                {
                    activeStrategyText.Text = "Запущено вручную: " + SelectedStrategy().Name;
                }
                else if (!string.IsNullOrWhiteSpace(runtime.InstalledStrategyName))
                {
                    activeStrategyText.Text = "Автозапуск настроен: " + runtime.InstalledStrategyName;
                }
                else
                {
                    activeStrategyText.Text = "Конфиг остановлен";
                }
            }

            suppressToggleEvents = true;
            if (serviceToggle != null) serviceToggle.IsChecked = manager.IsAppStartupEnabled();
            if (updateCheckToggle != null) updateCheckToggle.IsChecked = runtime.UpdateCheckEnabled;
            suppressToggleEvents = false;

            if (serviceTileText != null) serviceTileText.Text = runtime.ZapretServiceStatus;
            if (processTileText != null) processTileText.Text = runtime.WinwsRunning ? "запущен" : "остановлен";
            if (ipsetTileText != null) ipsetTileText.Text = runtime.IpsetStatus;
            if (gameTileText != null) gameTileText.Text = runtime.GameFilterStatus;

            if (startButton != null)
            {
                startButton.Content = active ? "Остановить конфиг" : "Запустить конфиг";
                startButton.Background = Brush(active ? theme.Danger : theme.Accent);
            }
        }

        private void RunAction(string title, Action action)
        {
            RunAction(title, delegate
            {
                action();
                return null;
            });
        }

        private void RunAction(string title, Func<string> action)
        {
            SetBusy(true);
            Log(title + "...");

            Task.Factory.StartNew(delegate
            {
                try
                {
                    var message = action();
                    Dispatcher.Invoke(new Action(delegate
                    {
                        if (!string.IsNullOrWhiteSpace(message)) Log(message);
                        Log("Готово: " + title);
                        SetBusy(false);
                        RefreshStatus();
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(new Action(delegate
                    {
                        Log("Ошибка: " + ex.Message);
                        SetBusy(false);
                        RefreshStatus();
                        ShowInfoDialog("Ошибка", ex.Message);
                    }));
                }
            });
        }

        private void SetBusy(bool busy)
        {
            if (testsRunning && busy) return;

            foreach (var button in actionButtons)
            {
                button.IsEnabled = !busy;
            }

            if (pageSubtitle != null)
            {
                pageSubtitle.Text = busy ? "Выполняется операция..." : SubtitleFor(currentPage);
            }
        }

        private void SetTestRunning(bool running)
        {
            testsRunning = running;

            foreach (var button in actionButtons)
            {
                button.IsEnabled = !running;
            }

            if (testStartButton != null) testStartButton.IsEnabled = !running;
            if (testCancelButton != null) testCancelButton.IsEnabled = running;

            if (pageSubtitle != null)
            {
                pageSubtitle.Text = running ? "Идет тест стратегий..." : SubtitleFor(currentPage);
            }
        }

        private void LoadStrategies()
        {
            if (strategyBox == null) return;
            var selectedName = strategyBox.SelectedItem is StrategyInfo ? ((StrategyInfo)strategyBox.SelectedItem).Name : null;
            if (string.IsNullOrWhiteSpace(selectedName))
            {
                selectedName = LoadSelectedStrategyName();
            }
            var strategies = manager.GetStrategies();
            strategyBox.ItemsSource = strategies;
            if (!string.IsNullOrWhiteSpace(selectedName))
            {
                var selected = strategies.FirstOrDefault(x => x.Name == selectedName);
                if (selected != null) strategyBox.SelectedItem = selected;
            }
            if (strategyBox.SelectedIndex < 0 && strategyBox.Items.Count > 0) strategyBox.SelectedIndex = 0;
            var current = strategyBox.SelectedItem as StrategyInfo;
            if (current != null) SaveSelectedStrategyName(current.Name);
        }

        private void OnStrategySelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var strategy = strategyBox == null ? null : strategyBox.SelectedItem as StrategyInfo;
            if (strategy == null) return;
            SaveSelectedStrategyName(strategy.Name);
        }

        private StrategyInfo SelectedStrategy()
        {
            var strategy = strategyBox == null ? null : strategyBox.SelectedItem as StrategyInfo;
            if (strategy != null) return strategy;

            var first = manager.GetStrategies().FirstOrDefault();
            if (first == null) throw new InvalidOperationException("Стратегии не найдены.");
            return first;
        }

        private void LoadEditorFile()
        {
            if (editorFileBox == null || editorBox == null) return;
            var item = editorFileBox.SelectedItem as ComboItem;
            if (item == null) return;

            try
            {
                editorBox.Text = manager.ReadTextFile(item.Value);
                Log("Загружен файл: " + item.Value);
            }
            catch (Exception ex)
            {
                editorBox.Text = "";
                Log("Не удалось загрузить файл: " + ex.Message);
            }
        }

        private UIElement BuildGameFilterSettings()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());

            var stack = new StackPanel();
            gameFilterMode = GameFilterModeValue(GameFilterSelectedIndex(manager.GetGameFilterStatus()));
            gameFilterModeLabel = new TextBlock
            {
                Text = "Режим: " + GameFilterModeTitle(GameFilterModeIndex(gameFilterMode)),
                Foreground = Brush(theme.Text),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            stack.Children.Add(gameFilterModeLabel);

            gameFilterModeButtons.Clear();
            var modes = new UniformGrid { Columns = 4 };
            modes.Children.Add(GameFilterModeButton("off", "Выкл"));
            modes.Children.Add(GameFilterModeButton("all", "TCP + UDP"));
            modes.Children.Add(GameFilterModeButton("tcp", "TCP"));
            modes.Children.Add(GameFilterModeButton("udp", "UDP"));
            ApplyUniformGridSpacing(modes, 8);
            stack.Children.Add(modes);
            grid.Children.Add(stack);

            return grid;
        }

        private UIElement BuildIpsetSettings()
        {
            var row = new UniformGrid { Columns = 2 };
            row.Children.Add(ActionButton("Переключить режим IPSet", OnIpsetClick, false));
            row.Children.Add(ActionButton("Обновить IPSet", OnUpdateIpsetClick, true));
            ApplyUniformGridSpacing(row, 10);
            return row;
        }

        private UIElement BuildAppUpdateActions()
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = "Установленная версия: " + manager.GetInstalledAppVersion(),
                Foreground = Brush(theme.Text),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var row = new UniformGrid { Columns = 1 };
            row.Children.Add(ActionButton("Проверить приложение", OnCheckAppUpdatesClick, true));
            ApplyUniformGridSpacing(row, 10);
            panel.Children.Add(row);
            return panel;
        }

        private UIElement BuildZapretUpdateActions()
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = "Установленная версия: " + manager.GetInstalledZapretVersion(),
                Foreground = Brush(theme.Text),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var row = new UniformGrid { Columns = 2 };
            row.Children.Add(ActionButton("Проверить и скачать", OnCheckUpdatesClick, true));
            row.Children.Add(ActionButton("Откатить обновление", OnRollbackUpdateClick, false));
            ApplyUniformGridSpacing(row, 10);
            panel.Children.Add(row);
            return panel;
        }

        private UIElement BuildAboutAppSettings()
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = "Zapret",
                Foreground = Brush(theme.Text),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8)
            });

            panel.Children.Add(new TextBlock
            {
                Text = "Автор интерфейса: NIKOLAEV-ANDREI",
                Foreground = Brush(theme.Text),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            });

            panel.Children.Add(MutedText("Репозиторий: github.com/NIKOLAEV-ANDREI/zapret-ui"));
            panel.Children.Add(MutedText("Основа проекта: Flowseal/zapret-discord-youtube."));
            panel.Children.Add(MutedText("Интерфейс управляет запуском конфигов, диагностикой, списками, обновлениями и откатом zapret."));
            return panel;
        }

        private UIElement BuildFeedbackSettings()
        {
            var panel = new StackPanel();

            var row = new UniformGrid { Columns = 1 };
            row.Children.Add(ActionButton("Сообщить об ошибке", OnFeedbackClick, true));
            ApplyUniformGridSpacing(row, 10);
            panel.Children.Add(row);
            return panel;
        }

        private UIElement BuildMaintenanceActions()
        {
            var panel = new StackPanel();

            var row1 = new UniformGrid { Columns = 1 };
            row1.Children.Add(ActionButton("Открыть hosts", OnHostsClick, false));
            ApplyUniformGridSpacing(row1, 10);
            panel.Children.Add(row1);

            var row2 = new UniformGrid { Columns = 2, Margin = new Thickness(0, 10, 0, 0) };
            row2.Children.Add(ActionButton("Создать ярлык", OnShortcutClick, false));
            row2.Children.Add(ActionButton("Удалить службу и WinDivert", OnRemoveServicesClick, false));
            ApplyUniformGridSpacing(row2, 10);
            panel.Children.Add(row2);

            var row3 = new UniformGrid { Columns = 1, Margin = new Thickness(0, 10, 0, 0) };
            row3.Children.Add(ActionButton("Старая диагностика", OnDiagnosticsClick, false));
            ApplyUniformGridSpacing(row3, 10);
            panel.Children.Add(row3);

            return panel;
        }

        private void OnFeedbackClick(object sender, RoutedEventArgs e)
        {
            var url = manager.GetFeedbackBotUrl();
            if (string.IsNullOrWhiteSpace(url))
            {
                ShowInfoDialog("Бот недоступен", "Не удалось открыть Telegram-бота обратной связи. Попробуйте позже.");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ShowInfoDialog("Не удалось открыть ссылку", ex.Message);
            }
        }

        private int GameFilterSelectedIndex(string status)
        {
            if (status == null) return 0;
            var normalized = status.ToLowerInvariant();
            if (normalized.Contains("tcp") && normalized.Contains("udp")) return 1;
            if (normalized.Contains("tcp")) return 2;
            if (normalized.Contains("udp")) return 3;
            return 0;
        }

        private Button GameFilterModeButton(string value, string text)
        {
            var button = new Button();
            button.Tag = value;
            button.Content = text;
            button.Height = 42;
            button.Cursor = Cursors.Hand;
            button.Click += OnGameFilterModeClick;
            button.Style = ButtonStyle(Brush(theme.SurfaceAlt), Brush(theme.AccentSoft), Brush(theme.Text), new CornerRadius(12), true);
            gameFilterModeButtons.Add(button);
            UpdateGameFilterModeButton(button);
            return button;
        }

        private void OnGameFilterModeClick(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var value = button == null ? null : button.Tag as string;
            if (string.IsNullOrWhiteSpace(value)) return;
            if (string.Equals(value, gameFilterMode, StringComparison.OrdinalIgnoreCase)) return;

            var previous = gameFilterMode;
            var title = "Изменить Game Filter?";
            var message = "Новый режим: " + GameFilterModeTitle(GameFilterModeIndex(value)) + ". Изменение применится после перезапуска конфига.";
            if (!ShowConfirmationDialog(title, message, "Применить", false))
            {
                gameFilterMode = previous;
                UpdateGameFilterModeUi();
                return;
            }

            gameFilterMode = value;
            UpdateGameFilterModeUi();
            Log("Game Filter меняет параметры будущего запуска. Для применения перезапусти конфиг или переустанови службу.");
            RunAction("Применение Game Filter", delegate { manager.SetGameFilter(gameFilterMode); });
        }

        private void UpdateGameFilterModeButton(Button button)
        {
            var selected = string.Equals(button.Tag as string, gameFilterMode, StringComparison.OrdinalIgnoreCase);
            button.Background = Brush(selected ? theme.Accent : theme.SurfaceAlt);
            button.Foreground = Brush(selected ? theme.ButtonText : theme.Text);
        }

        private void UpdateGameFilterModeUi()
        {
            if (gameFilterModeLabel != null)
            {
                gameFilterModeLabel.Text = "Режим: " + GameFilterModeTitle(GameFilterModeIndex(gameFilterMode));
            }

            foreach (var item in gameFilterModeButtons)
            {
                UpdateGameFilterModeButton(item);
            }
        }

        private string GameFilterModeValue(int index)
        {
            if (index == 1) return "all";
            if (index == 2) return "tcp";
            if (index == 3) return "udp";
            return "off";
        }

        private int GameFilterModeIndex(string mode)
        {
            if (mode == "all") return 1;
            if (mode == "tcp") return 2;
            if (mode == "udp") return 3;
            return 0;
        }

        private string GameFilterModeTitle(int index)
        {
            if (index == 1) return "TCP и UDP";
            if (index == 2) return "только TCP";
            if (index == 3) return "только UDP";
            return "выключен";
        }

        private void Log(string text)
        {
            if (logBox == null) return;
            logBox.Visibility = Visibility.Visible;
            logBox.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text + Environment.NewLine);
            logBox.ScrollToEnd();
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            RefreshStatus();
            Log("Статус обновлен");
        }

        private void OnStartClick(object sender, RoutedEventArgs e)
        {
            var strategy = SelectedStrategy();
            RunAction("Запуск стратегии " + strategy.Name, delegate { manager.StartStandalone(strategy); });
        }

        private void OnStopClick(object sender, RoutedEventArgs e)
        {
            RunAction("Остановка конфига", delegate { manager.StopProtection(); });
        }

        private void OnPowerClick(object sender, RoutedEventArgs e)
        {
            var runtime = manager.GetRuntimeStatus();
            var active = runtime.WinwsRunning || runtime.ZapretServiceRunning;

            if (active)
            {
                RunAction("Остановка конфига", delegate { manager.StopProtection(); });
            }
            else
            {
                var strategy = SelectedStrategy();
                RunAction("Запуск стратегии " + strategy.Name, delegate { manager.StartStandalone(strategy); });
            }
        }

        private void OnInstallServiceClick(object sender, RoutedEventArgs e)
        {
            var strategy = SelectedStrategy();
            RunAction("Установка службы " + strategy.Name, delegate { manager.InstallService(strategy); });
        }

        private void OnRemoveServicesClick(object sender, RoutedEventArgs e)
        {
            RunAction("Удаление служб", delegate { manager.RemoveServices(); });
        }

        private void OnIpsetClick(object sender, RoutedEventArgs e)
        {
            RunAction("Переключение IPSet", delegate { manager.SwitchIpset(); });
        }

        private void OnUpdateIpsetClick(object sender, RoutedEventArgs e)
        {
            RunAction("Обновление IPSet", delegate { return manager.UpdateIpset(); });
        }

        private void OnCheckAppUpdatesClick(object sender, RoutedEventArgs e)
        {
            CheckAppUpdate(true, false);
        }

        private void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
        {
            CheckZapretUpdate(true);
        }

        private void BeginAutoUpdateCheck()
        {
            if (updateCheckStarted || !manager.IsUpdateCheckEnabled()) return;
            updateCheckStarted = true;

            Dispatcher.BeginInvoke(new Action(delegate
            {
                CheckAppUpdate(false, true);
            }));
        }

        private void ShowPendingAppUpdateNotice()
        {
            var notice = manager.PopAppUpdateInstalledNotice();
            if (!string.IsNullOrWhiteSpace(notice))
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    Log(notice);
                    ShowInfoDialog("Обновление установлено", notice);
                }));
            }
        }

        private void CheckAppUpdate(bool manual, bool checkZapretAfter)
        {
            SetBusy(true);
            Log("Проверка обновлений приложения...");

            Task.Factory.StartNew(delegate
            {
                try
                {
                    var update = manager.GetAvailableAppUpdate();
                    Dispatcher.Invoke(new Action(delegate
                    {
                        SetBusy(false);
                        RefreshStatus();

                        if (!update.HasUpdate)
                        {
                            var message = "Установлена актуальная версия приложения: " + update.CurrentVersion;
                            Log(message);
                            if (manual) ShowInfoDialog("Обновления приложения", message);
                            if (checkZapretAfter) CheckZapretUpdate(false);
                            return;
                        }

                        PromptInstallAppUpdate(update, checkZapretAfter);
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(new Action(delegate
                    {
                        SetBusy(false);
                        RefreshStatus();
                        Log("Ошибка проверки обновления приложения: " + ex.Message);
                        if (manual) ShowInfoDialog("Ошибка проверки обновления приложения", ex.Message);
                        if (checkZapretAfter) CheckZapretUpdate(false);
                    }));
                }
            });
        }

        private void PromptInstallAppUpdate(UpdateInfo update, bool checkZapretAfter)
        {
            var current = string.IsNullOrWhiteSpace(update.CurrentVersion) ? "неизвестно" : update.CurrentVersion;
            var message = "Текущая версия: " + current + Environment.NewLine +
                          "Новая версия: " + update.LatestVersion;
            if (!string.IsNullOrWhiteSpace(update.Notes))
            {
                message += Environment.NewLine + Environment.NewLine + update.Notes;
            }

            if (!ShowConfirmationDialog("Доступна новая версия приложения", message, "Скачать и установить", false))
            {
                Log("Обновление приложения отменено пользователем.");
                if (checkZapretAfter) CheckZapretUpdate(false);
                return;
            }

            SetBusy(true);
            Log("Скачивание обновления приложения...");

            Task.Factory.StartNew(delegate
            {
                try
                {
                    var result = manager.PrepareAppUpdate(update);
                    Dispatcher.Invoke(new Action(delegate
                    {
                        SetBusy(false);
                        Log(result);
                        ShowInfoDialog("Обновление скачано", "Новая версия приложения скачана. Сейчас Zapret закроется, установит обновление и запустится снова.");
                        manager.StartAppUpdateInstaller();
                        bypassCloseConfirmation = true;
                        Close();
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(new Action(delegate
                    {
                        SetBusy(false);
                        RefreshStatus();
                        Log("Ошибка обновления приложения: " + ex.Message);
                        ShowInfoDialog("Ошибка обновления приложения", ex.Message);
                    }));
                }
            });
        }

        private void CheckZapretUpdate(bool manual)
        {
            SetBusy(true);
            Log("Проверка обновлений zapret...");

            Task.Factory.StartNew(delegate
            {
                try
                {
                    var update = manager.GetAvailableUpdate();
                    Dispatcher.Invoke(new Action(delegate
                    {
                        SetBusy(false);
                        RefreshStatus();

                        if (!update.HasUpdate)
                        {
                            var message = "Установлена актуальная версия zapret: " + update.CurrentVersion;
                            Log(message);
                            if (manual) ShowInfoDialog("Обновления", message);
                            return;
                        }

                        PromptInstallZapretUpdate(update);
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(new Action(delegate
                    {
                        SetBusy(false);
                        RefreshStatus();
                        Log("Ошибка проверки обновлений: " + ex.Message);
                        if (manual) ShowInfoDialog("Ошибка проверки обновлений", ex.Message);
                    }));
                }
            });
        }

        private void PromptInstallZapretUpdate(UpdateInfo update)
        {
            var current = string.IsNullOrWhiteSpace(update.CurrentVersion) ? "неизвестно" : update.CurrentVersion;
            var message = "Текущая версия: " + current + Environment.NewLine +
                          "Новая версия: " + update.LatestVersion + Environment.NewLine +
                          "Приложение скачает zip-релиз, сохранит backup текущих файлов и подключит новую версию zapret.";

            if (!ShowConfirmationDialog("Доступна новая версия zapret", message, "Скачать и установить", false))
            {
                Log("Обновление zapret отменено пользователем.");
                return;
            }

            RunAction("Обновление zapret до " + update.LatestVersion, delegate { return manager.InstallZapretUpdate(update); });
        }

        private void OnRollbackUpdateClick(object sender, RoutedEventArgs e)
        {
            if (!ShowConfirmationDialog("Откатить обновление zapret?", "Приложение восстановит последнюю сохраненную копию файлов zapret. Пользовательские списки и настройки UI не будут затронуты.", "Откатить", true))
            {
                return;
            }

            RunAction("Откат обновления zapret", delegate { return manager.RollbackZapretUpdate(); });
        }

        private void OnHostsClick(object sender, RoutedEventArgs e)
        {
            RunAction("Подготовка hosts", delegate { return manager.UpdateHosts(); });
        }

        private void OnDiagnosticsClick(object sender, RoutedEventArgs e)
        {
            RunAction("Диагностика", delegate { return Environment.NewLine + manager.RunDiagnostics(); });
        }

        private void OnTestsClick(object sender, RoutedEventArgs e)
        {
            RunAction("Запуск тестов", delegate { manager.LaunchTests(); });
        }

        private void OnRunInAppTestsClick(object sender, RoutedEventArgs e)
        {
            if (testsRunning) return;

            if (IsSelectedTestScope() && (testStrategyList == null || testStrategyList.SelectedItems.Count == 0))
            {
                ShowInfoDialog("Конфиги не выбраны", "Выбран режим тестирования только отмеченных конфигов. Выдели один или несколько конфигов в списке слева и запусти тест снова.");
                return;
            }

            var strategies = GetSelectedTestStrategies();
            var targets = testRunner.LoadTargets();
            var total = Math.Max(1, strategies.Count * targets.Count);

            testRows.Clear();
            UpdateTestSummaries(false);
            cancelTests = false;
            SetTestRunning(true);
            if (testProgressBar != null) testProgressBar.Value = 0;
            if (testProgressText != null) testProgressText.Text = "Подготовка теста...";

            Log("Запуск встроенного теста: стратегий " + strategies.Count + ", целей " + targets.Count);

            Task.Factory.StartNew(delegate
            {
                var completed = 0;
                try
                {
                    testRunner.Run(strategies, targets, delegate { return cancelTests; }, delegate(TestProgress progress)
                    {
                        Dispatcher.Invoke(new Action(delegate
                        {
                            if (!string.IsNullOrWhiteSpace(progress.Message) && testProgressText != null)
                            {
                                testProgressText.Text = progress.Message;
                            }

                            if (progress.Row != null)
                            {
                                testRows.Add(progress.Row);
                                completed++;
                                UpdateTestSummaries(false);
                                if (testProgressBar != null)
                                {
                                    testProgressBar.Value = Math.Min(100, completed * 100.0 / total);
                                }
                            }
                        }));
                    });

                    Dispatcher.Invoke(new Action(delegate
                    {
                        if (testProgressText != null) testProgressText.Text = cancelTests ? "Тест отменен." : "Тест завершен.";
                        if (testProgressBar != null && !cancelTests) testProgressBar.Value = 100;
                        UpdateTestSummaries(!cancelTests);
                        SetTestRunning(false);
                        RefreshStatus();
                        Log(cancelTests ? "Встроенный тест отменен" : "Встроенный тест завершен");
                    }));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(new Action(delegate
                    {
                        if (testProgressText != null) testProgressText.Text = "Ошибка теста.";
                        SetTestRunning(false);
                        RefreshStatus();
                        Log("Ошибка теста: " + ex.Message);
                        ShowInfoDialog("Ошибка теста", ex.Message);
                    }));
                }
            });
        }

        private void OnCancelTestsClick(object sender, RoutedEventArgs e)
        {
            if (!testsRunning) return;
            cancelTests = true;
            manager.StopStandalone();
            if (testProgressText != null) testProgressText.Text = "Отмена теста...";
            Log("Запрошена отмена встроенного теста");
        }

        private void OnSaveTestResultsClick(object sender, RoutedEventArgs e)
        {
            if (testRows.Count == 0)
            {
                ShowInfoDialog("Нет результатов", "Нет результатов для сохранения.");
                return;
            }

            RunAction("Сохранение результатов теста", delegate { return "Результаты сохранены: " + testRunner.SaveResults(testRows); });
        }

        private void OnRecommendedStrategyClick(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var name = button == null ? null : button.Tag as string;
            if (string.IsNullOrWhiteSpace(name)) return;

            ShowRecommendedStrategyDetails(name);
        }

        private void SelectStrategyByName(string name)
        {
            if (strategyBox == null) return;
            foreach (var item in strategyBox.Items)
            {
                var strategy = item as StrategyInfo;
                if (strategy != null && strategy.Name == name)
                {
                    strategyBox.SelectedItem = strategy;
                    return;
                }
            }
        }

        private string LoadSelectedStrategyName()
        {
            try
            {
                if (!File.Exists(selectedStrategyFile)) return null;
                return File.ReadAllText(selectedStrategyFile, Encoding.UTF8).Trim();
            }
            catch
            {
                return null;
            }
        }

        private void SaveSelectedStrategyName(string name)
        {
            try
            {
                Directory.CreateDirectory(paths.Utils);
                File.WriteAllText(selectedStrategyFile, name ?? "", new UTF8Encoding(false));
            }
            catch { }
        }

        private bool IsSidebarCollapsedSaved()
        {
            try
            {
                return File.Exists(sidebarStateFile);
            }
            catch
            {
                return false;
            }
        }

        private void SaveSidebarCollapsed(bool collapsed)
        {
            try
            {
                Directory.CreateDirectory(paths.Utils);
                if (collapsed)
                {
                    File.WriteAllText(sidebarStateFile, "COLLAPSED", Encoding.ASCII);
                }
                else if (File.Exists(sidebarStateFile))
                {
                    File.Delete(sidebarStateFile);
                }
            }
            catch { }
        }

        private void LoadPersistedState()
        {
            try
            {
                if (!File.Exists(stateFile)) return;

                foreach (var line in File.ReadAllLines(stateFile, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split('\t');
                    if (parts.Length < 7) continue;
                    testRows.Add(new TestRow
                    {
                        Strategy = DecodeField(parts[0]),
                        Target = DecodeField(parts[1]),
                        Http = DecodeField(parts[2]),
                        Tls12 = DecodeField(parts[3]),
                        Tls13 = DecodeField(parts[4]),
                        Ping = DecodeField(parts[5]),
                        Status = DecodeField(parts[6])
                    });
                }

                recommendedStrategies = BuildTestSummaries().Take(3).ToList();
            }
            catch
            {
                testRows.Clear();
                recommendedStrategies.Clear();
            }
        }

        private void SavePersistedTestState()
        {
            try
            {
                Directory.CreateDirectory(paths.Utils);
                var lines = testRows.Select(x => string.Join("\t", new[]
                {
                    EncodeField(x.Strategy),
                    EncodeField(x.Target),
                    EncodeField(x.Http),
                    EncodeField(x.Tls12),
                    EncodeField(x.Tls13),
                    EncodeField(x.Ping),
                    EncodeField(x.Status)
                }));
                File.WriteAllLines(stateFile, lines, new UTF8Encoding(false));
            }
            catch { }
        }

        private static string EncodeField(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
        }

        private static string DecodeField(string value)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(value ?? ""));
            }
            catch
            {
                return "";
            }
        }

        private void ShowRecommendedStrategyDetails(string name)
        {
            var rows = testRows
                .Where(x => string.Equals(x.Strategy, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var summary = recommendedStrategies.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? BuildTestSummaries().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

            if (rows.Count == 0 || summary == null)
            {
                ShowInfoDialog("Результат теста", "Детали теста для этой стратегии не найдены. Запусти диагностику заново.");
                return;
            }

            var dialog = new Window
            {
                Title = "Результат теста: " + name,
                Owner = this,
                Width = 720,
                Height = 460,
                MinWidth = 720,
                MaxWidth = 720,
                MinHeight = 460,
                MaxHeight = 460,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Brush(theme.Background),
                FontFamily = FontFamily,
                FontSize = FontSize
            };

            var root = new DockPanel { Margin = new Thickness(18) };
            dialog.Content = root;

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var useButton = DialogButton("Выбрать стратегию", delegate
            {
                dialog.Close();
                Navigate(PageKind.Home);
                SelectStrategyByName(name);
                Log("Выбрана рекомендованная стратегия: " + name);
            }, true);
            var closeButton = DialogButton("Закрыть", delegate { dialog.Close(); }, false);
            footer.Children.Add(useButton);
            footer.Children.Add(closeButton);
            ApplyHorizontalSpacing(footer, 10);

            var panel = new StackPanel();
            root.Children.Add(panel);

            panel.Children.Add(new TextBlock
            {
                Text = name,
                Foreground = Brush(theme.Text),
                FontSize = 22,
                FontWeight = FontWeights.SemiBold
            });
            panel.Children.Add(new TextBlock
            {
                Text = summary.Percent + "% успешно. Целей: " + summary.Targets + ". Ошибок: " + summary.FailedTargets + ".",
                Foreground = Brush(theme.Muted),
                Margin = new Thickness(0, 4, 0, 14)
            });

            var grid = new DataGrid
            {
                Height = 300,
                AutoGenerateColumns = false,
                IsReadOnly = true,
                ItemsSource = rows,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                CanUserResizeColumns = false,
                CanUserSortColumns = false,
                RowHeaderWidth = 0
            };
            StyleDataGrid(grid);
            grid.Columns.Add(TestColumn("Цель", "Target", 210));
            grid.Columns.Add(TestColumn("HTTP", "Http", 72));
            grid.Columns.Add(TestColumn("TLS 1.2", "Tls12", 78));
            grid.Columns.Add(TestColumn("TLS 1.3", "Tls13", 78));
            grid.Columns.Add(TestColumn("Ping", "Ping", 88));
            grid.Columns.Add(TestColumn("Статус", "Status", 82));
            panel.Children.Add(grid);

            dialog.ShowDialog();
        }

        private IList<StrategyInfo> GetSelectedTestStrategies()
        {
            var all = manager.GetStrategies();

            if (IsSelectedTestScope() && testStrategyList != null && testStrategyList.SelectedItems.Count > 0)
            {
                return testStrategyList.SelectedItems.Cast<StrategyInfo>().ToList();
            }

            if (IsSelectedTestScope())
            {
                throw new InvalidOperationException("Выбран режим \"Только выбранные\", но стратегии не выделены.");
            }

            return all;
        }

        private bool IsSelectedTestScope()
        {
            return testScopeBox != null
                && testScopeBox.SelectedItem is ComboItem
                && string.Equals(((ComboItem)testScopeBox.SelectedItem).Value, "selected", StringComparison.OrdinalIgnoreCase);
        }

        private void OnSaveEditorClick(object sender, RoutedEventArgs e)
        {
            var item = editorFileBox == null ? null : editorFileBox.SelectedItem as ComboItem;
            if (item == null) return;
            var relativePath = item.Value;
            var text = editorBox.Text;
            RunAction("Сохранение " + relativePath, delegate { manager.WriteTextFile(relativePath, text); });
        }

        private void OnToggleUpdateCheckClick(object sender, RoutedEventArgs e)
        {
            RunAction("Переключение авто-проверки", delegate { manager.ToggleUpdateCheck(); });
        }

        private void OnUpdateCheckToggleChanged(object sender, RoutedEventArgs e)
        {
            if (suppressToggleEvents) return;
            var desired = updateCheckToggle != null && updateCheckToggle.IsChecked == true;
            if (manager.IsUpdateCheckEnabled() == desired) return;

            RunAction("Переключение авто-проверки", delegate { manager.ToggleUpdateCheck(); });
        }

        private void OnShortcutClick(object sender, RoutedEventArgs e)
        {
            RunAction("Создание ярлыка", delegate { manager.CreateDesktopShortcut(); });
        }

        private void OnThemeClick(object sender, RoutedEventArgs e)
        {
            isDarkTheme = !isDarkTheme;
            manager.SetDarkTheme(isDarkTheme);
            theme = isDarkTheme ? DarkPalette() : LightPalette();
            BuildUi();
            Navigate(currentPage);
            Log(isDarkTheme ? "Включена темная тема" : "Включена светлая тема");
        }

        private void OnSidebarToggleClick(object sender, RoutedEventArgs e)
        {
            sidebarCollapsed = !sidebarCollapsed;
            SaveSidebarCollapsed(sidebarCollapsed);
            BuildUi();
            Navigate(currentPage);
        }

        private void OnThemeToggleChanged(object sender, RoutedEventArgs e)
        {
            if (suppressToggleEvents) return;
            OnThemeClick(sender, e);
        }

        private void OnServiceToggleChanged(object sender, RoutedEventArgs e)
        {
            if (suppressToggleEvents) return;
            var enabled = serviceToggle != null && serviceToggle.IsChecked == true;
            RunAction("Переключение автозапуска приложения", delegate { manager.SetAppStartup(enabled); });
        }

        private Button NavButton(string icon, string text, PageKind page)
        {
            var button = SidebarButton(icon, text, page, delegate { Navigate(page); });
            navButtons.Add(button);
            return button;
        }

        private Button SidebarButton(string icon, string text, PageKind? page, RoutedEventHandler handler)
        {
            var button = new Button();
            button.Tag = page;
            button.Width = sidebarCollapsed ? 42 : double.NaN;
            button.Height = 42;
            button.HorizontalAlignment = sidebarCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = sidebarCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            button.Margin = sidebarCollapsed ? new Thickness(0, 0, 0, 10) : new Thickness(0, 0, 0, 8);
            button.Padding = sidebarCollapsed ? new Thickness(0) : new Thickness(14, 0, 14, 0);
            button.Background = Brushes.Transparent;
            button.Foreground = Brush(theme.Muted);
            button.BorderBrush = Brushes.Transparent;
            button.Cursor = Cursors.Hand;
            button.ToolTip = text;
            button.Click += handler;
            button.Style = ButtonStyle(Brushes.Transparent, Brush(theme.SurfaceAlt), Brush(theme.Text), new CornerRadius(sidebarCollapsed ? 16 : 12), sidebarCollapsed);

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = sidebarCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left
            };
            row.Children.Add(new TextBlock
            {
                Text = icon,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 17,
                Width = 24,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            if (!sidebarCollapsed)
            {
                row.Children.Add(new TextBlock
                {
                    Text = text,
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            button.Content = row;
            return button;
        }

        private Button ActionButton(string text, RoutedEventHandler handler, bool primary)
        {
            var button = new Button();
            button.Content = text;
            button.MinHeight = 40;
            button.MinWidth = 150;
            button.Margin = new Thickness(0);
            button.Padding = new Thickness(16, 0, 16, 0);
            button.Cursor = Cursors.Hand;
            button.Click += handler;
            button.Style = ButtonStyle(
                Brush(primary ? theme.Accent : theme.SurfaceAlt),
                Brush(primary ? theme.Success : theme.Stroke),
                Brush(primary ? theme.ButtonText : theme.Text),
                new CornerRadius(12),
                true);
            actionButtons.Add(button);
            return button;
        }

        private Button DialogButton(string text, RoutedEventHandler handler, bool primary)
        {
            var button = new Button();
            button.Content = text;
            button.MinHeight = 38;
            button.MinWidth = 108;
            button.Padding = new Thickness(14, 0, 14, 0);
            button.Cursor = Cursors.Hand;
            button.Click += handler;
            button.Style = ButtonStyle(
                Brush(primary ? theme.Accent : theme.SurfaceAlt),
                Brush(primary ? theme.Success : theme.Stroke),
                Brush(primary ? theme.ButtonText : theme.Text),
                new CornerRadius(12),
                true);
            return button;
        }

        private Button PowerButton(string text, RoutedEventHandler handler)
        {
            var button = new Button();
            button.Content = text;
            button.Height = 58;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.FontSize = 16;
            button.FontWeight = FontWeights.SemiBold;
            button.Cursor = Cursors.Hand;
            button.Click += handler;
            button.Style = ButtonStyle(Brush(theme.Accent), Brush(theme.Success), Brush(theme.ButtonText), new CornerRadius(18), true);
            actionButtons.Add(button);
            return button;
        }

        private Button IconButton(string icon, string tooltip, RoutedEventHandler handler)
        {
            var button = new Button();
            button.Content = icon;
            button.Width = 42;
            button.Height = 42;
            button.ToolTip = tooltip;
            button.FontSize = 20;
            button.FontWeight = FontWeights.SemiBold;
            button.Cursor = Cursors.Hand;
            button.Click += handler;
            button.Style = ButtonStyle(Brush(theme.SurfaceAlt), Brush(theme.AccentSoft), Brush(theme.Text), new CornerRadius(21), true);
            actionButtons.Add(button);
            return button;
        }

        private Button TitleBarButton(string text, string tooltip, RoutedEventHandler handler, bool close)
        {
            var button = new Button();
            button.Content = text;
            button.Width = 46;
            button.Height = 37;
            button.ToolTip = tooltip;
            button.FontSize = close ? 18 : 20;
            button.FontWeight = FontWeights.SemiBold;
            button.Cursor = Cursors.Hand;
            button.Click += handler;
            button.Style = TitleBarButtonStyle(close);
            return button;
        }

        private CheckBox Toggle(string text, RoutedEventHandler changed)
        {
            var toggle = new CheckBox();
            toggle.Content = text;
            toggle.Foreground = Brush(theme.Text);
            toggle.FontWeight = FontWeights.SemiBold;
            toggle.VerticalAlignment = VerticalAlignment.Center;
            toggle.Cursor = Cursors.Hand;
            toggle.Style = ToggleStyle();
            toggle.Checked += changed;
            toggle.Unchecked += changed;
            return toggle;
        }

        private CheckBox SettingsToggle(string title, string description, bool isChecked, RoutedEventHandler changed)
        {
            var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
            text.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = Brush(theme.Text),
                FontWeight = FontWeights.SemiBold
            });
            text.Children.Add(new TextBlock
            {
                Text = description,
                Foreground = Brush(theme.Muted),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });

            var toggle = new CheckBox();
            toggle.Content = text;
            toggle.IsChecked = isChecked;
            toggle.Cursor = Cursors.Hand;
            toggle.Style = ToggleCardStyle();
            toggle.Checked += changed;
            toggle.Unchecked += changed;
            toggle.Margin = new Thickness(0, 0, 0, 12);
            return toggle;
        }

        private Border SettingsBlock(string title, string description, UIElement content)
        {
            var card = Card();
            card.Margin = new Thickness(0, 0, 0, 12);
            card.Padding = new Thickness(16);

            var stack = new StackPanel();
            card.Child = stack;
            stack.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = Brush(theme.Text),
                FontWeight = FontWeights.SemiBold
            });
            stack.Children.Add(new TextBlock
            {
                Text = description,
                Foreground = Brush(theme.Muted),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 12)
            });
            stack.Children.Add(content);
            return card;
        }

        private static void ApplyUniformGridSpacing(UniformGrid grid, double gap)
        {
            for (var i = 0; i < grid.Children.Count; i++)
            {
                var element = grid.Children[i] as FrameworkElement;
                if (element == null) continue;

                var column = grid.Columns <= 0 ? i : i % grid.Columns;
                element.Margin = new Thickness(column == 0 ? 0 : gap / 2, 0, column == grid.Columns - 1 ? 0 : gap / 2, 0);
            }
        }

        private static void ApplyHorizontalSpacing(Panel panel, double gap)
        {
            for (var i = 0; i < panel.Children.Count; i++)
            {
                var element = panel.Children[i] as FrameworkElement;
                if (element == null) continue;
                element.Margin = new Thickness(i == 0 ? 0 : gap, 0, 0, 0);
            }
        }

        private static void ApplyVerticalSpacing(Panel panel, double gap)
        {
            for (var i = 0; i < panel.Children.Count; i++)
            {
                var element = panel.Children[i] as FrameworkElement;
                if (element == null) continue;
                element.Margin = new Thickness(0, i == 0 ? 0 : gap, 0, 0);
            }
        }

        private void UpdateNavSelection()
        {
            foreach (var button in navButtons)
            {
                var selected = button.Tag is PageKind && (PageKind)button.Tag == currentPage;
                button.Background = selected ? Brush(theme.AccentSoft) : Brushes.Transparent;
                button.Foreground = selected ? Brush(theme.Text) : Brush(theme.Muted);
            }
        }

        private Border Card()
        {
            return new Border
            {
                Background = Brush(theme.Surface),
                BorderBrush = Brush(theme.Stroke),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(16)
            };
        }

        private UIElement StatusTile(string title, string info, out TextBlock valueText)
        {
            var card = Card();
            card.Margin = new Thickness(0);
            card.Padding = new Thickness(14);
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            card.Child = grid;

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            var titleText = new TextBlock
            {
                Text = title,
                Foreground = Brush(theme.Muted),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(titleText, 0);
            header.Children.Add(titleText);

            var infoBadge = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = Brush(theme.SurfaceAlt),
                BorderBrush = Brush(theme.Stroke),
                BorderThickness = new Thickness(1),
                ToolTip = info,
                Margin = new Thickness(8, 0, 0, 0)
            };
            var infoText = new TextBlock
            {
                Text = "i",
                Foreground = Brush(theme.Accent),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            infoBadge.Child = infoText;
            Grid.SetColumn(infoBadge, 1);
            header.Children.Add(infoBadge);

            valueText = new TextBlock
            {
                Text = "-",
                Foreground = Brush(theme.Text),
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 7, 0, 0),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetRow(valueText, 1);
            grid.Children.Add(valueText);
            return card;
        }

        private TextBlock SectionTitle(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(theme.Text),
                Margin = new Thickness(0, 0, 0, 8)
            };
        }

        private TextBlock MutedText(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = Brush(theme.Muted),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 8)
            };
        }

        private ScrollViewer Scroll(UIElement child)
        {
            return new ScrollViewer
            {
                Content = child,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
        }

        private GroupStyle CreateStrategyGroupStyle()
        {
            var groupStyle = new GroupStyle();
            var template = new DataTemplate();

            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, Brush(theme.AccentSoft));
            border.SetValue(Border.BorderBrushProperty, Brush(theme.Stroke));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            border.SetValue(Border.PaddingProperty, new Thickness(12, 7, 12, 7));
            border.SetValue(Border.MarginProperty, new Thickness(0, 10, 0, 4));

            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetValue(TextBlock.ForegroundProperty, Brush(theme.Text));
            text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            text.SetBinding(TextBlock.TextProperty, new Binding("Name") { StringFormat = "Конфиг: {0}" });
            border.AppendChild(text);

            template.VisualTree = border;
            groupStyle.HeaderTemplate = template;
            return groupStyle;
        }

        private DataGridTextColumn TestColumn(string header, string binding, double width)
        {
            var column = new DataGridTextColumn();
            column.Header = header;
            column.Binding = new Binding(binding);
            column.Width = new DataGridLength(width);
            column.MinWidth = width;

            var textStyle = new Style(typeof(TextBlock));
            textStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brush(theme.Text)));
            textStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
            textStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(8, 0, 8, 0)));
            textStyle.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            column.ElementStyle = textStyle;

            return column;
        }

        private void UpdateTestSummaries(bool final)
        {
            if (testSummaryPanel == null) return;

            testSummaryPanel.Children.Clear();
            var summaries = BuildTestSummaries();

            if (summaries.Count == 0)
            {
                if (final)
                {
                    recommendedStrategies.Clear();
                    SavePersistedTestState();
                }
                if (bestConfigText != null)
                {
                    bestConfigText.Text = "Лучшие конфиги появятся после завершения теста.";
                    bestConfigText.Foreground = Brush(theme.Muted);
                }
                return;
            }

            var title = SectionTitle(final ? "Итог по конфигам" : "Промежуточный итог");
            testSummaryPanel.Children.Add(title);

            var grid = new UniformGrid { Columns = 2 };
            foreach (var summary in summaries)
            {
                grid.Children.Add(TestSummaryCard(summary));
            }
            testSummaryPanel.Children.Add(grid);

            if (bestConfigText != null)
            {
                var best = summaries
                    .OrderByDescending(x => x.Percent)
                    .ThenByDescending(x => x.OkChecks)
                    .Take(3)
                    .ToList();

                if (final)
                {
                    recommendedStrategies = best;
                    SavePersistedTestState();
                }

                bestConfigText.Text = final
                    ? "Лучшие конфиги: " + string.Join(", ", best.Select(x => x.Name + " (" + x.Percent + "%)"))
                    : "Лучший сейчас: " + best[0].Name + " (" + best[0].Percent + "%)";
                bestConfigText.Foreground = Brush(theme.Text);
            }
        }

        private List<StrategySummary> BuildTestSummaries()
        {
            var result = new List<StrategySummary>();
            foreach (var group in testRows.GroupBy(x => x.Strategy))
            {
                var totalChecks = 0;
                var okChecks = 0;
                var failedTargets = 0;

                foreach (var row in group)
                {
                    AddCheckScore(row.Http, ref totalChecks, ref okChecks);
                    AddCheckScore(row.Tls12, ref totalChecks, ref okChecks);
                    AddCheckScore(row.Tls13, ref totalChecks, ref okChecks);
                    AddPingScore(row.Ping, ref totalChecks, ref okChecks);
                    if (!string.Equals(row.Status, "OK", StringComparison.OrdinalIgnoreCase))
                    {
                        failedTargets++;
                    }
                }

                var percent = totalChecks == 0 ? 0 : (int)Math.Round(okChecks * 100.0 / totalChecks);
                result.Add(new StrategySummary
                {
                    Name = group.Key,
                    Targets = group.Count(),
                    FailedTargets = failedTargets,
                    OkChecks = okChecks,
                    TotalChecks = totalChecks,
                    Percent = percent
                });
            }

            return result.OrderByDescending(x => x.Percent).ThenBy(x => x.FailedTargets).ThenBy(x => x.Name).ToList();
        }

        private UIElement TestSummaryCard(StrategySummary summary)
        {
            var color = summary.Percent >= 80 ? theme.Success : summary.Percent >= 50 ? theme.Warning : theme.Danger;

            var card = Card();
            card.Margin = new Thickness(0, 0, 12, 12);
            card.Padding = new Thickness(0);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            card.Child = grid;

            var stripe = new Border
            {
                Background = Brush(color),
                CornerRadius = new CornerRadius(18, 0, 0, 18)
            };
            grid.Children.Add(stripe);

            var stack = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);

            stack.Children.Add(new TextBlock
            {
                Text = summary.Name,
                Foreground = Brush(theme.Text),
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            stack.Children.Add(new TextBlock
            {
                Text = summary.Percent + "% успешно, целей: " + summary.Targets + ", ошибок: " + summary.FailedTargets,
                Foreground = Brush(theme.Muted),
                Margin = new Thickness(0, 5, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            return card;
        }

        private UIElement BuildRecommendedStrategiesPanel()
        {
            if (recommendedStrategies == null || recommendedStrategies.Count == 0) return null;

            var card = Card();
            card.Margin = new Thickness(0, 14, 0, 0);
            card.Padding = new Thickness(18);

            var stack = new StackPanel();
            card.Child = stack;
            stack.Children.Add(new TextBlock
            {
                Text = "Рекомендованные стратегии",
                Foreground = Brush(theme.Text),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold
            });
            stack.Children.Add(new TextBlock
            {
                Text = "По последней диагностике эти конфиги показали лучший результат.",
                Foreground = Brush(theme.Muted),
                Margin = new Thickness(0, 4, 0, 12),
                TextWrapping = TextWrapping.Wrap
            });

            var grid = new UniformGrid { Columns = Math.Min(3, recommendedStrategies.Count) };
            foreach (var summary in recommendedStrategies.Take(3))
            {
                grid.Children.Add(RecommendedStrategyCard(summary));
            }
            ApplyUniformGridSpacing(grid, 10);
            stack.Children.Add(grid);
            return card;
        }

        private UIElement RecommendedStrategyCard(StrategySummary summary)
        {
            var button = new Button();
            button.Tag = summary.Name;
            button.Cursor = Cursors.Hand;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.Padding = new Thickness(14);
            button.Style = ButtonStyle(Brush(theme.SurfaceAlt), Brush(theme.AccentSoft), Brush(theme.Text), new CornerRadius(14), false);
            button.Click += OnRecommendedStrategyClick;

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = summary.Name,
                Foreground = Brush(theme.Text),
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            stack.Children.Add(new TextBlock
            {
                Text = summary.Percent + "% успешно",
                Foreground = Brush(summary.Percent >= 80 ? theme.Success : theme.Warning),
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 6, 0, 0)
            });
            stack.Children.Add(new TextBlock
            {
                Text = "лучше запускать",
                Foreground = Brush(theme.Muted),
                Margin = new Thickness(0, 2, 0, 0)
            });

            button.Content = stack;
            return button;
        }

        private static void AddCheckScore(string value, ref int total, ref int ok)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "n/a" || value == "UNSUP") return;
            total++;
            if (value == "OK") ok++;
        }

        private static void AddPingScore(string value, ref int total, ref int ok)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "n/a") return;
            total++;
            if (value != "Timeout") ok++;
        }

        private void StyleTextBox(TextBox box, bool readOnly)
        {
            box.Background = Brush(readOnly ? theme.SurfaceAlt : theme.Surface);
            box.Foreground = Brush(theme.Text);
            box.BorderBrush = Brush(theme.Stroke);
            box.Padding = new Thickness(12);
            box.CaretBrush = Brush(theme.Text);
            ApplyThemeResources(box);
        }

        private void StyleCombo(ComboBox combo)
        {
            combo.Background = Brush(theme.SurfaceAlt);
            combo.Foreground = Brush(theme.Text);
            combo.BorderBrush = Brush(theme.Stroke);
            combo.Padding = new Thickness(12, 0, 38, 0);
            combo.MinHeight = Math.Max(combo.MinHeight, 38);
            ApplyThemeResources(combo);
            combo.Template = ComboBoxTemplate();

            var itemStyle = new Style(typeof(ComboBoxItem));
            itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brush(theme.Text)));
            itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 8, 12, 8)));
            itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            var selected = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, Brush(theme.AccentSoft)));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, Brush(theme.Text)));
            itemStyle.Triggers.Add(selected);
            var selectedItem = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
            selectedItem.Setters.Add(new Setter(Control.BackgroundProperty, Brush(theme.AccentSoft)));
            selectedItem.Setters.Add(new Setter(Control.ForegroundProperty, Brush(theme.Text)));
            itemStyle.Triggers.Add(selectedItem);
            combo.ItemContainerStyle = itemStyle;
        }

        private ControlTemplate ComboBoxTemplate()
        {
            var template = new ControlTemplate(typeof(ComboBox));

            var root = new FrameworkElementFactory(typeof(Grid));

            var toggle = new FrameworkElementFactory(typeof(ToggleButton));
            toggle.Name = "Toggle";
            toggle.SetValue(Control.BackgroundProperty, Brush(theme.SurfaceAlt));
            toggle.SetValue(Control.BorderBrushProperty, Brush(theme.Stroke));
            toggle.SetValue(Control.BorderThicknessProperty, new Thickness(1));
            toggle.SetValue(Control.PaddingProperty, new Thickness(0));
            toggle.SetValue(ToggleButton.FocusableProperty, false);
            toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsDropDownOpen")
            {
                RelativeSource = RelativeSource.TemplatedParent,
                Mode = BindingMode.TwoWay
            });
            toggle.SetValue(Control.TemplateProperty, ComboToggleTemplate());
            root.AppendChild(toggle);

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.Name = "ContentSite";
            content.SetValue(ContentPresenter.IsHitTestVisibleProperty, false);
            content.SetValue(ContentPresenter.MarginProperty, new Thickness(12, 0, 38, 0));
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemProperty));
            content.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemTemplateProperty));
            root.AppendChild(content);

            var popup = new FrameworkElementFactory(typeof(Popup));
            popup.Name = "PART_Popup";
            popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            popup.SetValue(Popup.FocusableProperty, false);
            popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.Fade);
            popup.SetBinding(Popup.IsOpenProperty, new Binding("IsDropDownOpen") { RelativeSource = RelativeSource.TemplatedParent });

            var popupBorder = new FrameworkElementFactory(typeof(Border));
            popupBorder.SetValue(Border.BackgroundProperty, Brush(theme.Surface));
            popupBorder.SetValue(Border.BorderBrushProperty, Brush(theme.Stroke));
            popupBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            popupBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
            popupBorder.SetValue(Border.PaddingProperty, new Thickness(4));
            popupBorder.SetValue(Border.MarginProperty, new Thickness(0, 6, 0, 0));
            popupBorder.SetBinding(FrameworkElement.MinWidthProperty, new Binding("ActualWidth") { RelativeSource = RelativeSource.TemplatedParent });

            var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
            scroll.SetValue(ScrollViewer.MaxHeightProperty, 320.0);
            scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);

            var items = new FrameworkElementFactory(typeof(ItemsPresenter));
            scroll.AppendChild(items);
            popupBorder.AppendChild(scroll);
            popup.AppendChild(popupBorder);
            root.AppendChild(popup);

            template.VisualTree = root;
            return template;
        }

        private ControlTemplate ComboToggleTemplate()
        {
            var template = new ControlTemplate(typeof(ToggleButton));

            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "Border";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));

            var grid = new FrameworkElementFactory(typeof(Grid));
            var arrow = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
            arrow.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 0 0 L 4 4 L 8 0 Z"));
            arrow.SetValue(System.Windows.Shapes.Path.FillProperty, Brush(theme.Muted));
            arrow.SetValue(FrameworkElement.WidthProperty, 8.0);
            arrow.SetValue(FrameworkElement.HeightProperty, 4.0);
            arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 14, 0));
            grid.AppendChild(arrow);
            border.AppendChild(grid);

            template.VisualTree = border;

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(theme.Accent)));
            template.Triggers.Add(hover);

            var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            checkedTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(theme.Accent)));
            template.Triggers.Add(checkedTrigger);

            return template;
        }

        private void StyleListBox(ListBox list)
        {
            list.Background = Brush(theme.Surface);
            list.Foreground = Brush(theme.Text);
            list.BorderBrush = Brush(theme.Stroke);
            ApplyThemeResources(list);

            var itemStyle = new Style(typeof(ListBoxItem));
            itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brush(theme.Surface)));
            itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brush(theme.Text)));
            itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 6, 10, 6)));
            var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, Brush(theme.AccentSoft)));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, Brush(theme.Text)));
            itemStyle.Triggers.Add(selected);
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, Brush(theme.SurfaceAlt)));
            itemStyle.Triggers.Add(hover);
            list.ItemContainerStyle = itemStyle;
        }

        private void StyleDataGrid(DataGrid grid)
        {
            grid.Background = Brush(theme.Surface);
            grid.Foreground = Brush(theme.Text);
            grid.BorderBrush = Brush(theme.Stroke);
            grid.RowBackground = Brush(theme.Surface);
            grid.AlternatingRowBackground = Brush(theme.SurfaceAlt);
            grid.HorizontalGridLinesBrush = Brush(theme.Stroke);
            grid.VerticalGridLinesBrush = Brush(theme.Stroke);
            ApplyThemeResources(grid);

            var rowStyle = new Style(typeof(DataGridRow));
            rowStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brush(theme.Surface)));
            rowStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brush(theme.Text)));
            var rowHover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            rowHover.Setters.Add(new Setter(Control.BackgroundProperty, Brush(theme.SurfaceAlt)));
            rowStyle.Triggers.Add(rowHover);
            grid.RowStyle = rowStyle;

            var cellStyle = new Style(typeof(DataGridCell));
            cellStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            cellStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brush(theme.Text)));
            cellStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(theme.Stroke)));
            cellStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4)));
            grid.CellStyle = cellStyle;

            var headerStyle = new Style(typeof(DataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brush(theme.SurfaceAlt)));
            headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brush(theme.Text)));
            headerStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(theme.Stroke)));
            headerStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 7, 8, 7)));
            grid.ColumnHeaderStyle = headerStyle;
        }

        private void ApplyThemeResources(FrameworkElement element)
        {
            element.Resources[SystemColors.WindowBrushKey] = Brush(theme.Surface);
            element.Resources[SystemColors.ControlBrushKey] = Brush(theme.Surface);
            element.Resources[SystemColors.ControlTextBrushKey] = Brush(theme.Text);
            element.Resources[SystemColors.WindowTextBrushKey] = Brush(theme.Text);
            element.Resources[SystemColors.HighlightBrushKey] = Brush(theme.AccentSoft);
            element.Resources[SystemColors.HighlightTextBrushKey] = Brush(theme.Text);
        }

        private Style ScrollBarStyle()
        {
            var xaml = @"
<Style xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
       xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
       TargetType=""{x:Type ScrollBar}"">
    <Setter Property=""Width"" Value=""0""/>
    <Setter Property=""Height"" Value=""0""/>
    <Setter Property=""MinWidth"" Value=""0""/>
    <Setter Property=""MinHeight"" Value=""0""/>
    <Setter Property=""Background"" Value=""Transparent""/>
    <Setter Property=""Template"">
        <Setter.Value>
            <ControlTemplate TargetType=""{x:Type ScrollBar}"">
                <Grid Width=""0"" Height=""0"" Opacity=""0""/>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>";

            return (Style)XamlReader.Parse(xaml);
        }

        private static string Hex(Color color)
        {
            return "#" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
        }

        private Style ToggleStyle()
        {
            var style = new Style(typeof(CheckBox));
            style.Setters.Add(new Setter(Control.TemplateProperty, ToggleTemplate(false)));
            return style;
        }

        private Style ToggleCardStyle()
        {
            var style = new Style(typeof(CheckBox));
            style.Setters.Add(new Setter(Control.TemplateProperty, ToggleTemplate(true)));
            return style;
        }

        private ControlTemplate ToggleTemplate(bool withCard)
        {
            var template = new ControlTemplate(typeof(CheckBox));

            var outer = new FrameworkElementFactory(typeof(Border));
            outer.Name = "Outer";
            outer.SetValue(Border.CornerRadiusProperty, new CornerRadius(withCard ? 16 : 19));
            outer.SetValue(Border.BackgroundProperty, Brush(withCard ? theme.SurfaceAlt : theme.Stroke));
            outer.SetValue(Border.BorderBrushProperty, Brush(theme.Stroke));
            outer.SetValue(Border.BorderThicknessProperty, new Thickness(withCard ? 1 : 0));
            outer.SetValue(Border.PaddingProperty, new Thickness(withCard ? 14 : 0));

            var dock = new FrameworkElementFactory(typeof(DockPanel));
            dock.SetValue(DockPanel.LastChildFillProperty, true);

            var track = new FrameworkElementFactory(typeof(Border));
            track.Name = "Track";
            track.SetValue(FrameworkElement.WidthProperty, 48.0);
            track.SetValue(FrameworkElement.HeightProperty, 26.0);
            track.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
            track.SetValue(Border.BackgroundProperty, Brush(theme.Stroke));
            track.SetValue(Border.MarginProperty, new Thickness(withCard ? 14 : 0, 0, 0, 0));
            track.SetValue(DockPanel.DockProperty, Dock.Right);

            var knob = new FrameworkElementFactory(typeof(Ellipse));
            knob.Name = "Knob";
            knob.SetValue(FrameworkElement.WidthProperty, 20.0);
            knob.SetValue(FrameworkElement.HeightProperty, 20.0);
            knob.SetValue(Shape.FillProperty, Brush(theme.Surface));
            knob.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            knob.SetValue(FrameworkElement.MarginProperty, new Thickness(3, 3, 3, 3));
            track.AppendChild(knob);

            dock.AppendChild(track);

            if (withCard)
            {
                var content = new FrameworkElementFactory(typeof(ContentPresenter));
                content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                dock.AppendChild(content);
            }

            outer.AppendChild(dock);
            template.VisualTree = outer;

            var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, Brush(theme.Accent), "Track"));
            checkedTrigger.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right, "Knob"));
            checkedTrigger.Setters.Add(new Setter(Shape.FillProperty, Brush(theme.ButtonText), "Knob"));
            if (withCard)
            {
                checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, Brush(theme.Accent), "Outer"));
            }
            template.Triggers.Add(checkedTrigger);

            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, Brush(theme.Accent), "Outer"));
            template.Triggers.Add(hoverTrigger);

            var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabledTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55, "Outer"));
            template.Triggers.Add(disabledTrigger);

            return template;
        }

        private Style ButtonStyle(Brush normal, Brush hover, Brush foreground, CornerRadius radius, bool center)
        {
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.BackgroundProperty, normal));
            style.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));

            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, radius);
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, center ? HorizontalAlignment.Center : HorizontalAlignment.Left);
            content.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            border.AppendChild(content);
            template.VisualTree = border;
            style.Setters.Add(new Setter(Control.TemplateProperty, template));

            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, hover));
            style.Triggers.Add(hoverTrigger);

            var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabledTrigger.Setters.Add(new Setter(Control.OpacityProperty, 0.45));
            style.Triggers.Add(disabledTrigger);

            return style;
        }

        private Style TitleBarButtonStyle(bool close)
        {
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brush(theme.Muted)));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));

            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);

            template.VisualTree = border;
            style.Setters.Add(new Setter(Control.TemplateProperty, template));

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, Brush(close ? theme.Danger : theme.SurfaceAlt)));
            hover.Setters.Add(new Setter(Control.ForegroundProperty, Brush(close ? theme.ButtonText : theme.Text)));
            style.Triggers.Add(hover);

            var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Control.OpacityProperty, 0.78));
            style.Triggers.Add(pressed);

            return style;
        }

        private void SetHeader(string title, string subtitle)
        {
            if (pageTitle != null) pageTitle.Text = title;
            if (pageSubtitle != null) pageSubtitle.Text = subtitle;
        }

        private string SubtitleFor(PageKind page)
        {
            if (page == PageKind.Home) return "Быстрый запуск и текущее состояние конфига";
            if (page == PageKind.Strategies) return "Профили запуска из файлов general*.bat";
            if (page == PageKind.Lists) return "Редактор пользовательских доменов, IP и целей тестов";
            if (page == PageKind.Diagnostics) return "Проверка конфликтов и запуск тестов";
            if (page == PageKind.Updates) return "IPSet, hosts и проверка новой версии";
            return "Тема, ярлык и параметры приложения";
        }

        private static Palette LightPalette()
        {
            return new Palette
            {
                Background = Color.FromRgb(244, 248, 251),
                Sidebar = Color.FromRgb(255, 255, 255),
                Surface = Color.FromRgb(255, 255, 255),
                SurfaceAlt = Color.FromRgb(234, 242, 248),
                Stroke = Color.FromRgb(210, 224, 235),
                Text = Color.FromRgb(34, 34, 34),
                Muted = Color.FromRgb(112, 117, 121),
                Accent = Color.FromRgb(51, 144, 236),
                AccentSoft = Color.FromRgb(229, 243, 255),
                Success = Color.FromRgb(49, 174, 96),
                Warning = Color.FromRgb(232, 156, 38),
                Danger = Color.FromRgb(229, 57, 53),
                ButtonText = Color.FromRgb(255, 255, 255)
            };
        }

        private static Palette DarkPalette()
        {
            return new Palette
            {
                Background = Color.FromRgb(23, 33, 43),
                Sidebar = Color.FromRgb(14, 22, 33),
                Surface = Color.FromRgb(24, 37, 51),
                SurfaceAlt = Color.FromRgb(36, 47, 61),
                Stroke = Color.FromRgb(43, 58, 74),
                Text = Color.FromRgb(245, 245, 245),
                Muted = Color.FromRgb(151, 166, 180),
                Accent = Color.FromRgb(42, 171, 238),
                AccentSoft = Color.FromRgb(33, 58, 80),
                Success = Color.FromRgb(49, 174, 96),
                Warning = Color.FromRgb(232, 156, 38),
                Danger = Color.FromRgb(239, 83, 80),
                ButtonText = Color.FromRgb(255, 255, 255)
            };
        }

        private static SolidColorBrush Brush(Color color)
        {
            return new SolidColorBrush(color);
        }

        private sealed class StrategySummary
        {
            public string Name;
            public int Targets;
            public int FailedTargets;
            public int OkChecks;
            public int TotalChecks;
            public int Percent;
        }

        private sealed class ComboItem
        {
            public string Value;
            public string Text;

            public ComboItem(string value, string text)
            {
                Value = value;
                Text = text;
            }

            public override string ToString()
            {
                return Text;
            }
        }
    }
}

