using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClassroomBreakLock.Auth;
using ClassroomBreakLock.Config;
using ClassroomBreakLock.Interop;
using ClassroomBreakLock.Logging;
using ClassroomBreakLock.Scheduling;

namespace ClassroomBreakLock.Views;

/// <summary>设置窗口：所有可配置项的集中入口。</summary>
public partial class SettingsWindow : Window
{
    private readonly AppConfig _working;   // 编辑副本，取消/关闭不影响运行中的配置
    private readonly AuthService _auth;
    private readonly ObservableCollection<ClassPeriod> _periods = new();
    private readonly ObservableCollection<UsbKey> _usbKeys = new();
    private readonly ObservableCollection<DateOverride> _overrides = new();
    private bool _loading;
    private string? _pendingEmergencyCode;

    /// <summary>保存后请求主程序热应用配置。</summary>
    public event Action<AppConfig>? ConfigSaved;

    /// <summary>请求刷新自检信息。</summary>
    public event Action? DiagnosticsRequested;

    public SettingsWindow(AppConfig cfg, AuthService auth)
    {
        InitializeComponent();
        _auth = auth;
        _working = cfg.Clone();
        _working.ConfigPath = cfg.ConfigPath;

        PeriodGrid.ItemsSource = _periods;
        UsbGrid.ItemsSource = _usbKeys;
        OverrideGrid.ItemsSource = _overrides;

        // ⚠ 坑：给 DayCombo 设 SelectedIndex 会立即触发 SelectionChanged。
        // 此时 _periods 还是空的，OnDayChanged → CommitDaySchedule 会把当天作息
        // 写回成一空列表（还会把 Enabled 写成 false）——默认作息就这样被抹掉了。
        // 所以初始化期间必须先立 _loading 挡住。
        _loading = true;
        try
        {
            DayCombo.ItemsSource = new[] { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
            DayCombo.SelectedIndex = Math.Min(6, Math.Max(0, (int)DateTime.Now.DayOfWeek));
        }
        finally
        {
            _loading = false;
        }

        // Win11 圆角：WPF 的 Border 不会裁剪子元素，表格圆角得自己上 Clip
        RoundClip(PeriodGrid, 7);
        RoundClip(UsbGrid, 7);
        RoundClip(OverrideGrid, 7);

        Loaded += (_, _) =>
        {
            // Win11 视觉：圆角 + 浅色标题栏（不支持的系统静默跳过）
            WindowEffects.ApplyRoundedCorners(this);
            WindowEffects.ApplyLightTitleBar(this);
            LoadIntoUi();
        };
    }

    private void LoadIntoUi()
    {
        _loading = true;
        try
        {
            ConfigPathText.Text = string.IsNullOrEmpty(_working.ConfigPath) ? "" : _working.ConfigPath;

            ClassroomNameBox.Text = _working.ClassroomName;
            EnabledCheck.IsChecked = _working.Enabled;
            TestModeCheck.IsChecked = _working.TestMode;
            LockTitleBox.Text = _working.Alerts.LockTitle;
            LockSubtitleBox.Text = _working.Alerts.LockSubtitle;
            EarlyUnlockBox.Text = _working.EarlyUnlockMinutes.ToString();
            LockDelayBox.Text = _working.LockDelaySeconds.ToString();
            AutoUnlockBreakCheck.IsChecked = _working.AutoUnlockDuringBreak;
            AllowButtonUnlockCheck.IsChecked = _working.Auth.AllowButtonUnlockWithoutAuth;
            UnlockDurationBox.Text = _working.Auth.UnlockDurationMinutes.ToString();

            // 认证
            _usbKeys.Clear();
            foreach (var k in _working.Auth.UsbKeys) _usbKeys.Add(k);

            PwdEnabledCheck.IsChecked = _working.Auth.Password.Enabled;
            PwdHintBox.Text = _working.Auth.Password.Hint;
            PwdStateText.Text = _working.Auth.Password.IsConfigured
                ? $"已设置密码（PBKDF2 {_working.Auth.Password.Iterations} 次迭代）"
                : "尚未设置密码";

            TotpEnabledCheck.IsChecked = _working.Auth.Totp.Enabled;
            TotpSecretBox.Text = _working.Auth.Totp.Secret;
            UpdateOtpUri();

            EmergencyEnabledCheck.IsChecked = _working.Auth.Emergency.Enabled;
            EmergencyOneTimeCheck.IsChecked = _working.Auth.Emergency.OneTimeUse;
            EmergencyStateText.Text = _working.Auth.Emergency.ConsumedAt is { Length: > 0 } t
                ? $"应急码已于 {t} 使用并作废，请生成新的"
                : _working.Auth.Emergency.IsConfigured ? "应急码已配置" : "尚未生成应急码";

            // 悬浮按钮
            FloatEnabledCheck.IsChecked = _working.FloatingButton.Enabled;
            FloatTextBox.Text = _working.FloatingButton.Text;
            MarginRightBox.Text = _working.FloatingButton.MarginRight.ToString();
            MarginBottomBox.Text = _working.FloatingButton.MarginBottom.ToString();
            SizeBox.Text = _working.FloatingButton.Size.ToString();
            OpacityBox.Text = _working.FloatingButton.Opacity.ToString("0.00");
            FadeBox.Text = _working.FloatingButton.FadeAfterIdleSeconds.ToString();
            DraggableCheck.IsChecked = _working.FloatingButton.Draggable;

            // 安全
            TopMostCheck.IsChecked = _working.Security.TopMost;
            BlockHotkeyCheck.IsChecked = _working.Security.BlockSystemHotkeys;
            ExcludeCaptureCheck.IsChecked = _working.Security.ExcludeFromCapture;
            WatchdogCheck.IsChecked = _working.Security.WatchdogEnabled;
            WatchdogIntervalBox.Text = _working.Security.WatchdogIntervalSeconds.ToString();

            // 日志
            LogEnabledCheck.IsChecked = _working.Logging.Enabled;
            LogDirBox.Text = _working.Logging.Directory;
            LogRetentionBox.Text = _working.Logging.RetentionDays.ToString();

            // ClassIsland 同步
            SyncEnabledCheck.IsChecked = _working.Sync.Enabled;
            SyncLockStartCheck.IsChecked = _working.Sync.LockOnClassStart;
            SyncUnlockEndCheck.IsChecked = _working.Sync.UnlockOnClassEnd;
            SyncPortBox.Text = _working.Sync.Port.ToString();
            SyncTokenBox.Text = _working.Sync.Token;
            SyncStateText.Text = _working.Sync.Enabled
                ? $"接收地址：http://127.0.0.1:{_working.Sync.Port}/class/start 与 /class/end（保存后生效）"
                : "当前未启用。启用并保存后，ClassIsland 即可向本机推送上下课事件。";

            _overrides.Clear();
            foreach (var o in _working.Overrides) _overrides.Add(o);

            LoadDaySchedule();
            RefreshDrives();
        }
        finally
        {
            _loading = false;
        }
        UpdateVerdict();
    }

    private void LoadDaySchedule()
    {
        var idx = DayCombo.SelectedIndex;
        if (idx < 0 || idx >= _working.Weekly.Count) return;

        var day = _working.Weekly[idx];
        DayEnabledCheck.IsChecked = day.Enabled;

        _periods.Clear();
        foreach (var p in day.Periods) _periods.Add(p.Clone());

        RebuildWeekOverview();
    }

    /// <summary>
    /// 整周概览：一屏看到七天各配了多少节，点一行直接切过去编辑。
    /// 当前正在编辑的那天以编辑框里的实时内容为准（可能还没提交）。
    /// </summary>
    private void RebuildWeekOverview()
    {
        if (WeekOverviewPanel is null) return;

        WeekOverviewPanel.Children.Clear();
        int selected = DayCombo.SelectedIndex;

        for (int i = 0; i < _working.Weekly.Count; i++)
        {
            int index = i;
            bool isCurrent = index == selected;

            IReadOnlyList<ClassPeriod> periods = isCurrent
                ? _periods.ToList()
                : _working.Weekly[i].Periods;

            bool enabled = isCurrent
                ? DayEnabledCheck.IsChecked == true
                : _working.Weekly[i].Enabled;

            string dayName = i < DayCombo.Items.Count
                ? DayCombo.Items[i]?.ToString() ?? $"第{i}天"
                : $"第{i}天";

            Brush soft = (Brush)FindResource("AccentSoftBrush");
            Brush accent = (Brush)FindResource("AccentBrush");
            Brush ink = (Brush)FindResource("InkBrush");
            Brush ink2 = (Brush)FindResource("Ink2Brush");
            Brush ink3 = (Brush)FindResource("Ink3Brush");
            Brush hover = (Brush)FindResource("HoverBrush");

            var row = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 0, 3),
                Background = isCurrent ? soft : Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var nameText = new TextBlock
            {
                Text = dayName,
                FontSize = 13.5,
                FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = isCurrent ? accent : ink,
                VerticalAlignment = VerticalAlignment.Center
            };

            var detail = new TextBlock
            {
                Text = enabled
                    ? (periods.Count == 0
                        ? "无时段"
                        : $"{periods.Count} 节 · {periods[0].Start}~{periods[^1].End}")
                    : "休息日（不锁屏）",
                FontSize = 12.5,
                Foreground = ink2,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var subjects = new TextBlock
            {
                Text = enabled && periods.Count > 0
                    ? string.Join("　", periods.Take(8).Select(p => p.Name))
                      + (periods.Count > 8 ? " …" : "")
                    : "",
                FontSize = 12,
                Foreground = ink3,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            Grid.SetColumn(nameText, 0);
            Grid.SetColumn(detail, 1);
            Grid.SetColumn(subjects, 2);
            line.Children.Add(nameText);
            line.Children.Add(detail);
            line.Children.Add(subjects);

            row.Child = line;

            row.MouseLeftButtonUp += (_, _) =>
            {
                if (DayCombo.SelectedIndex == index) return;
                DayCombo.SelectedIndex = index;   // 触发 OnDayChanged -> 提交+载入
            };

            if (!isCurrent)
            {
                row.MouseEnter += (_, _) => row.Background = hover;
                row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            }

            WeekOverviewPanel.Children.Add(row);
        }
    }

    private void CommitDaySchedule()
    {
        if (_loading) return;   // 加载/初始化期间绝不把当前（可能是空的）列表写回配置
        var idx = DayCombo.SelectedIndex;
        if (idx < 0 || idx >= _working.Weekly.Count) return;

        // 先把 DataGrid 里正在编辑的单元格提交掉
        PeriodGrid.CommitEdit(DataGridEditingUnit.Row, true);

        _working.Weekly[idx].Enabled = DayEnabledCheck.IsChecked == true;
        _working.Weekly[idx].Periods = _periods.Select(p => p.Clone()).ToList();
    }

    private void OnDayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        CommitDaySchedule();
        LoadDaySchedule();
    }

    private void OnCopyWeekdays(object sender, RoutedEventArgs e)
    {
        // 用周一（索引1）的作息覆盖当前日
        var src = _working.Weekly[1];
        _periods.Clear();
        foreach (var p in src.Periods) _periods.Add(p.Clone());
        _loading = true; DayEnabledCheck.IsChecked = src.Enabled; _loading = false;
        RebuildWeekOverview();
    }

    private void OnCopyToWeekdays(object sender, RoutedEventArgs e)
    {
        CommitDaySchedule();
        var src = _periods.Select(p => p.Clone()).ToList();
        var srcEnabled = DayEnabledCheck.IsChecked == true;
        for (int d = 1; d <= 5; d++)
        {
            _working.Weekly[d].Enabled = srcEnabled;
            _working.Weekly[d].Periods = src.Select(p => p.Clone()).ToList();
        }
        Status("已把当前作息复制到周一~周五");
        RebuildWeekOverview();
    }

    private void OnAddPeriod(object sender, RoutedEventArgs e)
    {
        _periods.Add(new ClassPeriod { Name = "新时段", Start = "08:00", End = "08:45", LockDuring = true });
        RebuildWeekOverview();
    }

    private void OnDeletePeriod(object sender, RoutedEventArgs e)
    {
        if (PeriodGrid.SelectedItem is ClassPeriod p) _periods.Remove(p);
        RebuildWeekOverview();
    }

    /// <summary>把控件裁剪成圆角（Win11 观感）。Border 的 CornerRadius 不会裁剪子元素，所以只能自己上 Clip。</summary>
    private static void RoundClip(FrameworkElement element, double radius)
    {
        void Apply()
        {
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
            {
                return;
            }

            element.Clip = new RectangleGeometry(
                new Rect(0, 0, element.ActualWidth, element.ActualHeight), radius, radius);
        }

        element.SizeChanged += (_, _) => Apply();
        Apply();
    }

    // ---------------- 课表批量导入 ----------------

    private void OnImportFromFile(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择课表文件",
            Filter = "课表文件|*.xlsx;*.xlsm;*.csv;*.txt;*.yml;*.yaml|Excel 工作簿|*.xlsx;*.xlsm|CSV|*.csv|YAML|*.yml;*.yaml|文本文件|*.txt",
            CheckFileExists = true
        };

        if (dlg.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            ApplyImported(ScheduleImporter.ParseFile(dlg.FileName), System.IO.Path.GetFileName(dlg.FileName));
        }
        catch (Exception ex)
        {
            AppDialog.Error(this, "导入失败：" + ex.Message, "导入课表");
        }
    }

    private void OnImportFromPaste(object sender, RoutedEventArgs e)
    {
        string? text = ShowPasteDialog();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            ApplyImported(ScheduleImporter.ParseText(text), "粘贴内容");
        }
        catch (Exception ex)
        {
            AppDialog.Error(this, "解析失败：" + ex.Message, "导入课表");
        }
    }

    /// <summary>把解析结果填到当前星期的作息表里，并询问是否直接套用到工作日。</summary>
    private void ApplyImported(ScheduleImporter.Result result, string source)
    {
        if (result.TotalPeriodCount == 0)
        {
            string detail = result.Warnings.Count > 0
                ? "\n\n" + string.Join("\n", result.Warnings.Take(6))
                : "";

            AppDialog.Warn(this,
                $"没有从「{source}」里解析出任何时段。\n\n" +
                "· 表格/CSV/TXT：每行要含两个时间，例如「第1节 08:00 08:45」\n" +
                $"· YAML：支持 ClassIsland/CESE 的 schedules 结构{detail}",
                "导入课表");
            return;
        }

        string msg;
        int importedCount;

        if (result.HasWeekInfo)
        {
            // ClassIsland / CESE：带星期信息，直接铺满整周
            bool weekdaysOnly = WeekdaysOnlyCheck.IsChecked == true;
            var perDay = new List<string>();
            var ignored = new List<string>();
            importedCount = 0;

            foreach (ScheduleImporter.DayResult day in result.Days)
            {
                while (_working.Weekly.Count <= day.DayIndex)
                {
                    _working.Weekly.Add(new WeeklySchedule());
                }

                if (weekdaysOnly && day.DayIndex is 0 or 6)
                {
                    // 周末：清掉模板里的旧数据，按“不上课”处理
                    _working.Weekly[day.DayIndex].Enabled = false;
                    _working.Weekly[day.DayIndex].Periods = new List<ClassPeriod>();
                    ignored.Add($"{day.DayName}（{day.Periods.Count} 节）");
                    continue;
                }

                _working.Weekly[day.DayIndex].Enabled = day.Periods.Count > 0;
                _working.Weekly[day.DayIndex].Periods = day.Periods
                    .Select(p => p.Clone())
                    .ToList();

                importedCount += day.Periods.Count;
                perDay.Add($"{day.DayName} {day.Periods.Count} 节");
            }

            // 界面切到今天，方便立刻核对
            _loading = true;
            DayCombo.SelectedIndex = Math.Min(6, Math.Max(0, (int)DateTime.Now.DayOfWeek));
            _loading = false;
            LoadDaySchedule();

            msg = $"已导入 {perDay.Count} 天的作息，共 {importedCount} 个时段：\n\n"
                  + string.Join("、", perDay);

            if (ignored.Count > 0)
            {
                msg += "\n\n按你的设置忽略：" + string.Join("、", ignored);
            }

            msg += "\n\n单双周已合并为同一份作息（课间锁只看时间，不区分单双周）。";

            ImportStateText.Text = $"已导入：{string.Join("、", perDay)}"
                                   + (ignored.Count > 0 ? $"；已忽略 {string.Join("、", ignored)}" : "");
        }
        else
        {
            _periods.Clear();
            foreach (ClassPeriod p in result.Periods)
            {
                _periods.Add(p);
            }

            _loading = true;
            DayEnabledCheck.IsChecked = true;
            _loading = false;

            CommitDaySchedule();

            importedCount = result.Periods.Count;
            var dayName = DayCombo.SelectedItem as string ?? "当前这一天";

            msg = $"已从「{source}」解析出 {result.Periods.Count} 个时段（已按开始时间排序）。"
                  + "\n\n是否同时套用到周一~周五？";

            ImportStateText.Text = $"已导入：{dayName} {result.Periods.Count} 节";
        }

        if (result.Warnings.Count > 0)
        {
            msg += $"\n\n另有 {result.Warnings.Count} 行已跳过：\n" + string.Join("\n", result.Warnings.Take(6));
        }

        if (result.HasWeekInfo)
        {
            AppDialog.Info(this, msg, "导入完成");
        }
        else if (AppDialog.Confirm(this, msg, "导入课表", "套用到工作日", "只应用到当前这一天"))
        {
            OnCopyToWeekdays(this, new RoutedEventArgs());
        }

        Status($"已导入 {importedCount} 个时段");
        RebuildWeekOverview();
    }

    /// <summary>粘贴导入弹窗：改用自定义圆角弹窗（不再是系统灰框）。</summary>
    private string? ShowPasteDialog()
        => AppDialog.MultilineInput(
            this,
            "粘贴课表",
            "从 Excel 里框选区域 Ctrl+C，再粘到这里即可（制表符、逗号分隔都行）；" +
            "也可以直接粘贴 ClassIsland / CESE 导出的 YAML 内容。",
            initial: "");

    private void OnAddOverride(object sender, RoutedEventArgs e)
        => _overrides.Add(new DateOverride { Date = DateTime.Now.ToString("yyyy-MM-dd"), Note = "调休/放假" });

    private void OnDeleteOverride(object sender, RoutedEventArgs e)
    {
        if (OverrideGrid.SelectedItem is DateOverride o) _overrides.Remove(o);
    }

    // ---------------- U 盘 ----------------

    private void OnRefreshDrives(object sender, RoutedEventArgs e) => RefreshDrives();

    private void RefreshDrives()
    {
        var drives = UsbAuthService.EnumerateRemovableDrives();
        DriveListText.Text = drives.Count == 0
            ? "当前未检测到可移动磁盘。请插入 U 盘后点「刷新已插入 U 盘」。"
            : "检测到：" + string.Join("   |   ",
                drives.Select(d => $"{d.Display}  SN={d.VolumeSerial}  {d.FileSystem}  " +
                                   $"{d.TotalBytes / 1024.0 / 1024 / 1024:0.0}GB"));
    }

    private void OnRegisterUsb(object sender, RoutedEventArgs e)
    {
        var drives = UsbAuthService.EnumerateRemovableDrives();
        if (drives.Count == 0)
        {
            Warn("请先插入 U 盘");
            return;
        }

        var drive = drives.Count == 1 ? drives[0] : PromptChooseDrive(drives);
        if (drive is null) return;

        string label = Prompt("给这把钥匙起个名字", "钥匙U盘") ?? "钥匙U盘";
        string fileName = Prompt("密钥文件名（将写入 U 盘根目录）", "breaklock.key") ?? "breaklock.key";

        var (ok, msg, key) = UsbAuthService.RegisterKey(drive, label, fileName);
        if (ok && key is not null)
        {
            _usbKeys.Add(key);
            Status(msg);
        }
        else Warn(msg);
    }

    private RemovableDrive? PromptChooseDrive(List<RemovableDrive> drives)
    {
        // 多盘时简单用一个选择窗口
        var win = new Window
        {
            Title = "选择 U 盘",
            Width = 460,
            Height = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A)),
            Topmost = true
        };
        var list = new ListBox
        {
            Margin = new Thickness(16),
            ItemsSource = drives.Select(d => $"{d.Display}  SN={d.VolumeSerial}").ToList(),
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x21, 0x3A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xF7)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0x3E, 0x5E)),
            SelectedIndex = 0
        };
        var okBtn = new Button { Content = "确定", Width = 100, Height = 34, Margin = new Thickness(16, 0, 16, 16), HorizontalAlignment = HorizontalAlignment.Right };
        okBtn.Click += (_, _) => win.DialogResult = true;

        var dock = new DockPanel();
        DockPanel.SetDock(okBtn, Dock.Bottom);
        dock.Children.Add(okBtn);
        dock.Children.Add(list);
        win.Content = dock;

        return win.ShowDialog() == true && list.SelectedIndex >= 0
            ? drives[list.SelectedIndex] : null;
    }

    private void OnRemoveUsb(object sender, RoutedEventArgs e)
    {
        if (UsbGrid.SelectedItem is UsbKey k)
        {
            _usbKeys.Remove(k);
            Status($"已从列表移除「{k.Label}」，保存后生效");
        }
    }

    // ---------------- 密码 / TOTP / 应急码 ----------------

    private void OnSetPassword(object sender, RoutedEventArgs e)
    {
        var p1 = NewPwdBox.Password;
        var p2 = ConfirmPwdBox.Password;
        if (string.IsNullOrWhiteSpace(p1)) { Warn("密码不能为空"); return; }
        if (p1 != p2) { Warn("两次输入的密码不一致"); return; }
        if (p1.Length < 4) { Warn("密码至少 4 位"); return; }

        var (salt, hash) = HashUtil.CreateHash(p1, _working.Auth.Password.Iterations);
        _working.Auth.Password.Salt = salt;
        _working.Auth.Password.Hash = hash;
        _working.Auth.Password.Enabled = true;
        PwdEnabledCheck.IsChecked = true;
        PwdStateText.Text = $"已设置密码（PBKDF2 {_working.Auth.Password.Iterations} 次迭代）";
        NewPwdBox.Clear();
        ConfirmPwdBox.Clear();
        Status("密码已设置，保存后生效");
    }

    private void OnGenerateTotp(object sender, RoutedEventArgs e)
    {
        _working.Auth.Totp.Secret = Totp.GenerateSecret();
        TotpSecretBox.Text = _working.Auth.Totp.Secret;
        TotpEnabledCheck.IsChecked = true;
        UpdateOtpUri();
        Status("已生成新的 TOTP 密钥，请用验证器 App 扫码或手工录入");
    }

    private void UpdateOtpUri()
    {
        if (string.IsNullOrWhiteSpace(_working.Auth.Totp.Secret))
        {
            OtpUriText.Text = "尚未生成密钥";
            return;
        }
        OtpUriText.Text = Totp.BuildOtpAuthUri(
            _working.Auth.Totp.Secret, _working.Auth.Totp.Issuer, _working.Auth.Totp.Account);
    }

    private void OnCopyOtpUri(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(OtpUriText.Text)) return;
        try
        {
            Clipboard.SetText(OtpUriText.Text);
            Status("otpauth 链接已复制到剪贴板");
        }
        catch (Exception ex) { Warn($"复制失败：{ex.Message}"); }
    }

    private void OnGenerateEmergency(object sender, RoutedEventArgs e)
    {
        var code = HashUtil.GenerateReadableCode();
        var (salt, hash) = HashUtil.CreateHash(code, _working.Auth.Emergency.Iterations);
        _working.Auth.Emergency.Salt = salt;
        _working.Auth.Emergency.Hash = hash;
        _working.Auth.Emergency.ConsumedAt = null;
        _working.Auth.Emergency.Enabled = true;

        _pendingEmergencyCode = code;
        EmergencyCodeText.Text = code;
        EmergencyStateText.Text = "请立刻抄下这个应急码——保存后它不会再显示（只存哈希）";
        Status("已生成新的应急码");
    }

    // ---------------- 保存 ----------------

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            CommitDaySchedule();

            _working.ClassroomName = ClassroomNameBox.Text.Trim();
            _working.Enabled = EnabledCheck.IsChecked == true;
            _working.TestMode = TestModeCheck.IsChecked == true;
            _working.Alerts.LockTitle = LockTitleBox.Text;
            _working.Alerts.LockSubtitle = LockSubtitleBox.Text;
            _working.EarlyUnlockMinutes = ParseInt(EarlyUnlockBox.Text, 2, 0, 60);
            _working.LockDelaySeconds = ParseInt(LockDelayBox.Text, 5, 0, 600);
            _working.AutoUnlockDuringBreak = AutoUnlockBreakCheck.IsChecked == true;
            _working.Auth.AllowButtonUnlockWithoutAuth = AllowButtonUnlockCheck.IsChecked == true;
            _working.Auth.UnlockDurationMinutes = ParseInt(UnlockDurationBox.Text, 0, 0, 1440);

            _working.Auth.UsbKeys = _usbKeys.Select(k => k.Clone()).ToList();
            _working.Auth.Password.Enabled = PwdEnabledCheck.IsChecked == true;
            _working.Auth.Password.Hint = PwdHintBox.Text;
            _working.Auth.Totp.Enabled = TotpEnabledCheck.IsChecked == true;
            _working.Auth.Emergency.Enabled = EmergencyEnabledCheck.IsChecked == true;
            _working.Auth.Emergency.OneTimeUse = EmergencyOneTimeCheck.IsChecked == true;

            _working.FloatingButton.Enabled = FloatEnabledCheck.IsChecked == true;
            _working.FloatingButton.Text = string.IsNullOrWhiteSpace(FloatTextBox.Text) ? "下课" : FloatTextBox.Text.Trim();
            _working.FloatingButton.MarginRight = ParseInt(MarginRightBox.Text, 24, 0, 5000);
            _working.FloatingButton.MarginBottom = ParseInt(MarginBottomBox.Text, 96, 0, 5000);
            _working.FloatingButton.Size = ParseInt(SizeBox.Text, 84, 40, 260);
            _working.FloatingButton.Opacity = ParseDouble(OpacityBox.Text, 0.92, 0.1, 1.0);
            _working.FloatingButton.FadeAfterIdleSeconds = ParseInt(FadeBox.Text, 8, 0, 3600);
            _working.FloatingButton.Draggable = DraggableCheck.IsChecked == true;

            _working.Security.TopMost = TopMostCheck.IsChecked == true;
            _working.Security.BlockSystemHotkeys = BlockHotkeyCheck.IsChecked == true;
            _working.Security.ExcludeFromCapture = ExcludeCaptureCheck.IsChecked == true;
            _working.Security.WatchdogEnabled = WatchdogCheck.IsChecked == true;
            _working.Security.WatchdogIntervalSeconds = ParseInt(WatchdogIntervalBox.Text, 5, 1, 3600);

            _working.Logging.Enabled = LogEnabledCheck.IsChecked == true;
            _working.Logging.Directory = LogDirBox.Text.Trim();
            _working.Logging.RetentionDays = ParseInt(LogRetentionBox.Text, 90, 1, 3650);

            _working.Sync.Enabled = SyncEnabledCheck.IsChecked == true;
            _working.Sync.LockOnClassStart = SyncLockStartCheck.IsChecked == true;
            _working.Sync.UnlockOnClassEnd = SyncUnlockEndCheck.IsChecked == true;
            _working.Sync.Port = ParseInt(SyncPortBox.Text, 8731, 1024, 65535);
            _working.Sync.Token = SyncTokenBox.Text.Trim();

            _working.Overrides = _overrides.Select(o => new DateOverride
            {
                Date = o.Date, Locked = o.Locked, Note = o.Note
            }).ToList();

            ConfigStore.Save(_working);
            Log.Info("设置已保存并应用");
            ConfigSaved?.Invoke(_working.Clone());
            Status("已保存并应用 ✓");

            if (_pendingEmergencyCode is not null)
            {
                _pendingEmergencyCode = null;
                EmergencyCodeText.Text = "";
                AppDialog.Info(this,
                    "应急码已保存。请确认你已抄写下来，它不会再显示。",
                    "应急码");
            }

            UpdateVerdict();
        }
        catch (Exception ex)
        {
            Log.Error($"保存设置失败：{ex}");
            Warn($"保存失败：{ex.Message}");
        }
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var fresh = ConfigStore.Load(_working.ConfigPath);
            CopyInto(_working, fresh);
            LoadIntoUi();
            Status("已从磁盘重载配置");
        }
        catch (Exception ex) { Warn($"重载失败：{ex.Message}"); }
    }

    private static void CopyInto(AppConfig target, AppConfig src)
    {
        target.ClassroomName = src.ClassroomName;
        target.Enabled = src.Enabled;
        target.TestMode = src.TestMode;
        target.EarlyUnlockMinutes = src.EarlyUnlockMinutes;
        target.LockDelaySeconds = src.LockDelaySeconds;
        target.AutoUnlockDuringBreak = src.AutoUnlockDuringBreak;
        target.Security = src.Security;
        target.FloatingButton = src.FloatingButton;
        target.Alerts = src.Alerts;
        target.Logging = src.Logging;
        target.Auth = src.Auth;
        target.Weekly = src.Weekly;
        target.Overrides = src.Overrides;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnRefreshDiag(object sender, RoutedEventArgs e) => DiagnosticsRequested?.Invoke();

    /// <summary>由主程序回填自检结果。</summary>
    public void UpdateDiagnostics(bool topMost, bool hook, bool capture, int blockedKeys, bool isAdmin, bool isLocked)
    {
        DiagLockStateText.Text = isLocked
            ? "锁屏状态：● 正在锁屏"
            : "锁屏状态：○ 当前未锁屏（以下三项需在锁屏期间看）";
        DiagLockStateText.Foreground = isLocked
            ? new SolidColorBrush(Color.FromRgb(0x0F, 0x7B, 0x0F))
            : new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));

        if (isLocked)
        {
            DiagTopMostText.Text = $"置顶：{(topMost ? "✓ 已生效" : "✗ 未生效")}";
            DiagHookText.Text = $"键盘钩子：{(hook ? "✓ 已安装（Win/Alt+Tab/Alt+F4 已屏蔽）" : "✗ 未安装")}";
            DiagCaptureText.Text = $"捕获保护：{(capture ? "✓ 已从截屏/投屏排除" : "✗ 未启用")}";
        }
        else
        {
            DiagTopMostText.Text = "置顶：— 待锁屏时生效";
            DiagHookText.Text = "键盘钩子：— 待锁屏时安装";
            DiagCaptureText.Text = "捕获保护：— 待锁屏时启用";
        }
        DiagAdminText.Text = $"管理员权限：{(isAdmin ? "✓ 是（可压盖管理员程序）" : "○ 否（模板模式，对管理员程序可能压不住）")}";
        DiagBlockedText.Text = $"已拦截按键次数：{blockedKeys}";
    }

    private void UpdateVerdict()
    {
        try
        {
            var engine = new ScheduleEngine(_working);
            var v = engine.Evaluate(DateTime.Now);
            VerdictText.Text = "当前判定：" + v.Describe();
        }
        catch (Exception ex)
        {
            VerdictText.Text = $"判定失败：{ex.Message}";
        }
    }

    // ---------------- 小工具 ----------------

    private void Status(string msg)
    {
        StatusBarText.Text = $"{DateTime.Now:HH:mm:ss}  {msg}";
        StatusBarText.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
    }

    private void Warn(string msg)
    {
        StatusBarText.Text = $"{DateTime.Now:HH:mm:ss}  {msg}";
        StatusBarText.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
    }

    private static int ParseInt(string? s, int fallback, int min, int max)
        => int.TryParse(s, out var v) ? Math.Clamp(v, min, max) : fallback;

    private static double ParseDouble(string? s, double fallback, double min, double max)
        => double.TryParse(s, out var v) ? Math.Clamp(v, min, max) : fallback;

    private static string? Prompt(string message, string defaultValue)
    {
        var win = new Window
        {
            Title = message,
            Width = 420,
            Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A)),
            Topmost = true
        };
        var box = new TextBox
        {
            Text = defaultValue,
            Margin = new Thickness(16, 16, 16, 0),
            Height = 32,
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x21, 0x3A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xF7))
        };
        var btn = new Button { Content = "确定", Width = 100, Height = 32, Margin = new Thickness(16, 12, 16, 16), HorizontalAlignment = HorizontalAlignment.Right };
        btn.Click += (_, _) => win.DialogResult = true;

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = message, Margin = new Thickness(16, 16, 16, 0), Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0xA6, 0xC4)) });
        stack.Children.Add(box);
        stack.Children.Add(btn);
        win.Content = stack;

        return win.ShowDialog() == true ? box.Text : null;
    }

    private void OnOpenLogDir(object sender, RoutedEventArgs e)
    {
        var dir = string.IsNullOrWhiteSpace(_working.Logging.Directory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ClassroomBreakLock", "logs")
            : _working.Logging.Directory;
        OpenPath(dir);
    }

    private void OnOpenConfigDir(object sender, RoutedEventArgs e)
    {
        var path = _working.ConfigPath;
        OpenPath(string.IsNullOrEmpty(path) ? ConfigStore.DefaultConfigPath : path);
    }

    private static void OpenPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else
            {
                AppDialog.Warn(null, $"路径不存在：{path}", "打开路径");
            }
        }
        catch (Exception ex)
        {
            AppDialog.Error(null, $"打开失败：{ex.Message}", "打开路径");
        }
    }
}
