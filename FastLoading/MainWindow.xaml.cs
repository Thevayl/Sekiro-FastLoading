using System;
using System.IO;
using System.Timers;
using System.Windows;
using System.Threading;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Input;
using System.ComponentModel;
using System.Windows.Interop;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace FastLoading
{
    public partial class MainWindow : Window
    {
        internal Process _gameProc;
        internal IntPtr _gameHwnd = IntPtr.Zero;
        internal IntPtr _gameAccessHwnd = IntPtr.Zero;
        internal static IntPtr _gameAccessHwndStatic;
        internal long _offset_framelock = 0x0;

        internal MemoryCaveGenerator _memoryCaveGenerator;
        internal SettingsService _settingsService;

        internal readonly DispatcherTimer _dispatcherTimerGameCheck = new DispatcherTimer();
        internal readonly DispatcherTimer _dispatcherTimerFreezeMem = new DispatcherTimer();
        internal readonly BackgroundWorker _bgwScanGame = new BackgroundWorker();
        internal readonly System.Timers.Timer _timerStatsCheck = new System.Timers.Timer();
        internal bool _running = false;
        internal bool _gameInitializing = false;
        internal bool _dataCave_speedfix = false;
        internal bool _dataCave_fovsetting = false;
        internal bool _codeCave_camadjust = false;
        internal bool _codeCave_emblemupgrade = false;
        internal bool _retryAccess = true;
        internal bool _statLoggingEnabled = false;
        internal bool _initialStartup = true;
        internal bool _debugMode = false;
        internal RECT _windowRect;
        internal bool _isLegacyVersion = false;
        internal bool _inQuietMode = false;
        internal static string _path_logs;

        internal const string _DATACAVE_SPEEDFIX_POINTER = "speedfixPointer";
        
        /*
         * Thevayl's mod
         */
        internal long _offset_is_in_loading = 0x0;
        internal CancellationTokenSource FLST_cts = new CancellationTokenSource();
        internal readonly DispatcherTimer _dispatcherTimerGameStillAlive = new DispatcherTimer();
        internal CancellationToken FLST_token;
        internal Task FLST_thread = null;

        public MainWindow(bool quietMode)
        {
            _path_logs = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location) + @"\FastLoading.log";
            InitializeComponent();
            FLST_token = FLST_cts.Token;
            if (quietMode)
            {
                _inQuietMode = true;
                ShowInTaskbar = false;
                WindowState = WindowState.Minimized;
                Visibility = Visibility.Hidden;
            }
        }

        /// <summary>
        /// On window loaded.
        /// </summary>
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var mutex = new Mutex(true, "fastLoading", out bool isNewInstance);
            if (!isNewInstance)
            {
                MessageBox.Show("Another instance is already running!", "FastLoading", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                Environment.Exit(0);
            }
            GC.KeepAlive(mutex);

            try
            {
                HIGHCONTRAST highContrastInfo = new HIGHCONTRAST() { cbSize = Marshal.SizeOf(typeof(HIGHCONTRAST)) };
                if (SystemParametersInfo(SPI_GETHIGHCONTRAST, (uint)highContrastInfo.cbSize, ref highContrastInfo, 0))
                {
                    if ((highContrastInfo.dwFlags & HCF_HIGHCONTRASTON) == 1)
                    {
                        // high contrast mode is active, remove grid background color and let the OS handle it
                        gMainGrid.Background = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Could not fetch SystemParameters: " + ex.Message);
            }

            LoadConfiguration();

            IntPtr hWnd = new WindowInteropHelper(this).Handle;

            _bgwScanGame.DoWork += new DoWorkEventHandler(ReadGame);
            _bgwScanGame.RunWorkerCompleted += new RunWorkerCompletedEventHandler(OnReadGameFinish);

            _dispatcherTimerGameCheck.Tick += new EventHandler(async (object s, EventArgs a) =>
            {
                bool result = await CheckGame();
                if (result)
                {
                    UpdateStatus("scanning game...", Brushes.Orange);
                    _bgwScanGame.RunWorkerAsync();
                    _dispatcherTimerGameCheck.Stop();
                }
            });

            LogToFile("FastLoading started");

            _dispatcherTimerGameCheck.Interval = new TimeSpan(0, 0, 0, 0, 2000);
            _dispatcherTimerGameCheck.Start();
        }

        /// <summary>
        /// On window closing.
        /// </summary>
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            _timerStatsCheck.Stop();
            SaveConfiguration();
            IntPtr hWnd = new WindowInteropHelper(this).Handle;
            if (_gameAccessHwnd != IntPtr.Zero)
                CloseHandle(_gameAccessHwnd);
            if (FLST_thread != null)
                FLST_cts.Cancel();

            LogToFile("FastLoading stopped");
        }

        /// <summary>
        /// Load all saved settings from previous run.
        /// </summary>
        private void LoadConfiguration()
        {
            _settingsService = new SettingsService(Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location) + @"\FastLoading.xml");
            if (!_settingsService.Load()) return;

            this.cbFastLoading.IsChecked = _settingsService.ApplicationSettings.cbFastLoading;
        }

        /// <summary>
        /// Save all settings to configuration file.
        /// </summary>
        private void SaveConfiguration()
        {
            _settingsService.ApplicationSettings.cbFastLoading = this.cbFastLoading.IsChecked == true;

            _settingsService.Save();
        }

        /// <summary>
        /// Resets GUI and clears configuration file.
        /// </summary>
        private void ClearConfiguration()
        {
            this.cbFastLoading.IsChecked = true;
            _settingsService.Clear();
        }

        /// <summary>
        /// Checks if game is running and initializes further functionality.
        /// </summary>
        private Task<bool> CheckGame()
        {
            // game process have been found last check and can be read now, aborting
            if (_gameInitializing)
                return Task.FromResult(true);
                
            Process[] procList = Process.GetProcessesByName(GameData.PROCESS_NAME);
            if (procList.Length < 1)
                return Task.FromResult(false);

            if (_running || _offset_framelock != 0x0)
                return Task.FromResult(false);

            int gameIndex = -1;
            for (int i = 0; i < procList.Length; i++)
            {
                if (procList[i].MainWindowTitle != GameData.PROCESS_TITLE || !procList[i].MainModule.FileVersionInfo.FileDescription.Contains(GameData.PROCESS_DESCRIPTION))
                {
                    if(!(procList[i].MainModule.FileVersionInfo.FileDescription == GameData.PROCESS_DESCRIPTION2)) // Compatibility fix?
                        continue;
                }
                    
                gameIndex = i;
                break;
            }
            if (gameIndex < 0) // No valid game process found
            {
                UpdateStatus("no valid game process found...", Brushes.Red);
                LogToFile("no valid game process found...");
                for (int j = 0; j < procList.Length; j++)
                {
                    LogToFile(string.Format("\tProcess #{0}: '{1}' | ({2})", j, procList[j].MainModule.FileName, procList[j].MainModule.FileVersionInfo.FileName));
                    LogToFile(string.Format("\tDescription #{0}: {1} | {2} | {3}", j, procList[j].MainWindowTitle, procList[j].MainModule.FileVersionInfo.CompanyName, procList[j].MainModule.FileVersionInfo.FileDescription));
                    LogToFile(string.Format("\tData #{0}: {1} | {2} | {3} | {4} | {5}", j, procList[j].MainModule.FileVersionInfo.FileVersion, procList[j].MainModule.ModuleMemorySize, procList[j].StartTime, procList[j].Responding, procList[j].HasExited));
                }
                return Task.FromResult(false);
            }

            _gameProc = procList[gameIndex];
            _gameHwnd = procList[gameIndex].MainWindowHandle;
            _gameAccessHwnd = OpenProcess(PROCESS_ALL_ACCESS, false, (uint)procList[gameIndex].Id);
            _gameAccessHwndStatic = _gameAccessHwnd;
            if (_gameHwnd == IntPtr.Zero || _gameAccessHwnd == IntPtr.Zero || _gameProc.MainModule.BaseAddress == IntPtr.Zero)
            {
                LogToFile("no access to game...");
                LogToFile("hWnd: " + _gameHwnd.ToString("X"));
                LogToFile("Access hWnd: " + _gameAccessHwnd.ToString("X"));
                LogToFile("BaseAddress: " + procList[gameIndex].MainModule.BaseAddress.ToString("X"));
                if (!_retryAccess)
                {
                    UpdateStatus("no access to game...", Brushes.Red);
                    _dispatcherTimerGameCheck.Stop();
                    return Task.FromResult(false);
                }
                _gameHwnd = IntPtr.Zero;
                if (_gameAccessHwnd != IntPtr.Zero)
                {
                    CloseHandle(_gameAccessHwnd);
                    _gameAccessHwnd = IntPtr.Zero;
                    _gameAccessHwndStatic = IntPtr.Zero;
                }
                LogToFile("retrying...");
                _retryAccess = false;
                return Task.FromResult(false);
            }

            string gameFileVersion = FileVersionInfo.GetVersionInfo(procList[0].MainModule.FileName).FileVersion;
            if (gameFileVersion != GameData.PROCESS_EXE_VERSION)
            {
                if (Array.IndexOf(GameData.PROCESS_EXE_VERSION_SUPPORTED_LEGACY, gameFileVersion) < 0)
                {
                    if (!_settingsService.ApplicationSettings.gameVersionNotify)
                    {
                        MessageBox.Show(string.Format("Unknown game version '{0}'.\nSome functions might not work properly or even crash the game. " +
                                    "Check for updates on this utility regularly following the link at the bottom.", gameFileVersion), "FastLoading", MessageBoxButton.OK, MessageBoxImage.Warning);
                        ClearConfiguration();
                        _settingsService.ApplicationSettings.gameVersionNotify = true;
                        SaveConfiguration();
                    }
                }
                else
                {
                    _isLegacyVersion = true;
                    _settingsService.ApplicationSettings.gameVersionNotify = false;
                }
            }
            else
                _settingsService.ApplicationSettings.gameVersionNotify = false;

            // give the game some time to initialize
            _gameInitializing = true;
            UpdateStatus("game initializing...", Brushes.Orange);
            return Task.FromResult(false);
        }

        private Task<bool> CheckGame_still_alive()
        {
            Process[] procList = Process.GetProcessesByName(GameData.PROCESS_NAME);
            if (procList.Length < 1)
                return Task.FromResult(false);

            int gameIndex = -1;
            for (int i = 0; i < procList.Length; i++)
            {
                if (procList[i].MainWindowTitle != GameData.PROCESS_TITLE)
                    continue;
                gameIndex = i;
                break;
            }
            if (gameIndex < 0)
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }

        /// <summary>
        /// Read all game offsets and pointer (external).
        /// </summary>
        private void ReadGame(object sender, DoWorkEventArgs doWorkEventArgs)
        {
            PatternScan patternScan = new PatternScan(_gameAccessHwnd, _gameProc.MainModule);
            _memoryCaveGenerator = new MemoryCaveGenerator(_gameAccessHwnd, _gameProc.MainModule.BaseAddress.ToInt64());

            _offset_framelock = patternScan.FindPattern(GameData.PATTERN_FRAMELOCK) + GameData.PATTERN_FRAMELOCK_OFFSET;
            Debug.WriteLine("fFrameTick found at: 0x" + _offset_framelock.ToString("X"));
            if (!IsValidAddress(_offset_framelock))
            {
                _offset_framelock = patternScan.FindPattern(GameData.PATTERN_FRAMELOCK_FUZZY) + GameData.PATTERN_FRAMELOCK_FUZZY_OFFSET;
                Debug.WriteLine("2. fFrameTick found at: 0x" + _offset_framelock.ToString("X"));
            }
            if (!IsValidAddress(_offset_framelock))
                _offset_framelock = 0x0;

            long lpSpeedFixPointer = patternScan.FindPattern(GameData.PATTERN_FRAMELOCK_SPEED_FIX) + GameData.PATTERN_FRAMELOCK_SPEED_FIX_OFFSET;
            Debug.WriteLine("lpSpeedFixPointer at: 0x" + lpSpeedFixPointer.ToString("X"));
            if (IsValidAddress(lpSpeedFixPointer))
            {
                if (_memoryCaveGenerator.CreateNewDataCave(_DATACAVE_SPEEDFIX_POINTER, lpSpeedFixPointer, BitConverter.GetBytes(GameData.PATCH_FRAMELOCK_SPEED_FIX_DEFAULT_VALUE), PointerStyle.dwRelative))
                    _dataCave_speedfix = true;
                Debug.WriteLine("lpSpeedFixPointer data cave at: 0x" + _memoryCaveGenerator.GetDataCaveAddressByName(_DATACAVE_SPEEDFIX_POINTER).ToString("X"));
            }

            /*
             * Thevayl's mod
            */
            _offset_is_in_loading = patternScan.FindPattern(GameData.PATTERN_IS_IN_LOADING);
            Debug.WriteLine("isInLoadingScreen found at: 0x" + _offset_is_in_loading.ToString("X"));
            //LogToFile("isInLoadingScreen found at: 0x" + _offset_is_in_loading.ToString("X"));
            if (!IsValidAddress(_offset_is_in_loading))
                _offset_is_in_loading = 0x0;
        }

        /// <summary>
        /// All game data has been read.
        /// </summary>
        private void OnReadGameFinish(object sender, RunWorkerCompletedEventArgs runWorkerCompletedEventArgs)
        {
            if (_offset_framelock == 0x0)
            {
                UpdateStatus("frame tick not found...", Brushes.Red);
                LogToFile("frame tick not found...");
            }

            if (!_dataCave_speedfix)
            {
                UpdateStatus("could not create speed fix table...", Brushes.Red);
                LogToFile("could not create speed fix table...");
            }

            /*
             * Thevayl's mod
            */
            if (_offset_is_in_loading == 0x0)
            {
                UpdateStatus("loading screen check not found...", Brushes.Red);
                LogToFile("could not find loading offset...");
                this.cbFastLoading.IsEnabled = false;
            }

            _running = true;
            PatchGame();
        }

        /// <summary>
        /// Determines whether everything is ready for patching.
        /// </summary>
        /// <returns>True if we can patch game, false otherwise.</returns>
        private bool CanPatchGame()
        {
            if (!_running) return false;
            if (!_gameProc.HasExited) return true;

            _running = false;
            if (_gameAccessHwnd != IntPtr.Zero)
                CloseHandle(_gameAccessHwnd);
            _dispatcherTimerFreezeMem.Stop();
            _timerStatsCheck.Stop();
            _gameProc = null;
            _gameHwnd = IntPtr.Zero;
            _gameAccessHwnd = IntPtr.Zero;
            _gameAccessHwndStatic = IntPtr.Zero;
            _gameInitializing = false;
            _initialStartup = true;
            _offset_framelock = 0x0;
            _dataCave_speedfix = false;

            _memoryCaveGenerator.ClearCaves();
            _memoryCaveGenerator = null;
            UpdateStatus("waiting for game...", Brushes.White);
            _dispatcherTimerGameCheck.Start();

            return false;
        }

        /// <summary>
        /// Patch up this broken port of a game.
        /// </summary>
        private void PatchGame()
        {
            if (!CanPatchGame()) return;

            List<bool> results = new List<bool>
            {
                PatchFastLoading(false)
            };
            if (results.IndexOf(true) > -1)
                UpdateStatus(DateTime.Now.ToString("HH:mm:ss") + " Game patched!", Brushes.Green);
            else
                UpdateStatus(DateTime.Now.ToString("HH:mm:ss") + " Game unpatched!", Brushes.White);
            _initialStartup = false;

            if (_inQuietMode)
            {
                // Prevent thread from closing
                //Close();
                _dispatcherTimerGameStillAlive.Tick += new EventHandler(async (object s, EventArgs a) =>
                {
                    bool result = await CheckGame_still_alive();
                    if (!result)
                    {
                        _dispatcherTimerGameStillAlive.Stop();
                        Console.WriteLine("Game is dead, closing");
                        Close();
                    }
                });

                _dispatcherTimerGameStillAlive.Interval = new TimeSpan(0, 0, 0, 0, 5000);
                _dispatcherTimerGameStillAlive.Start();
            }
        }

        /// <summary>
        /// Returns the hexadecimal representation of an IEEE-754 floating point number
        /// </summary>
        /// <param name="input">The floating point number.</param>
        /// <returns>The hexadecimal representation of the input.</returns>
        private static string GetHexRepresentationFromFloat(float input)
        {
            uint f = BitConverter.ToUInt32(BitConverter.GetBytes(input), 0);
            return "0x" + f.ToString("X8");
        }

        /// <summary>
        /// Calculates DPI-clean resolution of the primary screen. Requires dpiAware in manifest.
        /// </summary>
        /// <returns></returns>
        private Size GetDpiSafeResolution()
        {
            PresentationSource presentationSource = PresentationSource.FromVisual(this);
            if (presentationSource == null)
                return new Size(SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
            Matrix matrix = presentationSource.CompositionTarget.TransformToDevice;
            return new Size(SystemParameters.PrimaryScreenWidth * matrix.M22, SystemParameters.PrimaryScreenHeight * matrix.M11);
        }

        /// <summary>
        /// Checks if an address is valid.
        /// </summary>
        /// <param name="address">The address (the pointer points to).</param>
        /// <returns>True if (pointer points to a) valid address.</returns>
        private static bool IsValidAddress(Int64 address)
        {
            return (address >= 0x10000 && address < 0x000F000000000000);
        }

        /// <summary>
        /// Reads a given type from processes memory using a generic method.
        /// </summary>
        /// <typeparam name="T">The base type to read.</typeparam>
        /// <param name="hProcess">The process handle to read from.</param>
        /// <param name="lpBaseAddress">The address to read from.</param>
        /// <returns>The given base type read from memory.</returns>
        /// <remarks>GCHandle and Marshal are costy.</remarks>
        private static T Read<T>(IntPtr hProcess, Int64 lpBaseAddress)
        {
            byte[] lpBuffer = new byte[Marshal.SizeOf(typeof(T))];
            ReadProcessMemory(hProcess, lpBaseAddress, lpBuffer, (ulong)lpBuffer.Length, out _);
            GCHandle gcHandle = GCHandle.Alloc(lpBuffer, GCHandleType.Pinned);
            T structure = (T)Marshal.PtrToStructure(gcHandle.AddrOfPinnedObject(), typeof(T));
            gcHandle.Free();
            return structure;
        }

        /// <summary>
        /// Writes a given type and value to processes memory using a generic method.
        /// </summary>
        /// <param name="hProcess">The process handle to read from.</param>
        /// <param name="lpBaseAddress">The address to write from.</param>
        /// <param name="bytes">The byte array to write.</param>
        /// <returns>True if successful, false otherwise.</returns>
        private static bool WriteBytes(IntPtr hProcess, Int64 lpBaseAddress, byte[] bytes)
        {
            return WriteProcessMemory(hProcess, lpBaseAddress, bytes, (ulong)bytes.Length, out _);
        }

        /// <summary>
        /// Gets the static offset to the referenced object instead of the offset from the instruction.
        /// </summary>
        /// <param name="hProcess">Handle to the process.</param>
        /// <param name="lpInstructionAddress">The address of the instruction.</param>
        /// <param name="instructionLength">The length of the instruction including the 4 bytes offset.</param>
        /// <remarks>Static pointers in x86-64 are relative offsets from their instruction address.</remarks>
        /// <returns>The static offset from the process to the referenced object.</returns>
        private static Int64 DereferenceStaticX64Pointer(IntPtr hProcess, Int64 lpInstructionAddress, int instructionLength)
        {
            return lpInstructionAddress + Read<Int32>(hProcess, lpInstructionAddress + (instructionLength - 0x04)) + instructionLength;
        }

        /// <summary>
        /// Check whether input is numeric only.
        /// </summary>
        /// <param name="text">The text to check.</param>
        /// <returns>True if input is numeric only.</returns>
        private static bool IsNumericInput(string text)
        {
            return Regex.IsMatch(text, "[^0-9]+");
        }

        /// <summary>
        /// Logs messages to log file.
        /// </summary>
        /// <param name="msg">The message to write to file.</param>
        internal static void LogToFile(string msg)
        {
            string timedMsg = "[" + DateTime.Now + "] " + msg;
            Debug.WriteLine(timedMsg);
            try
            {
                using (StreamWriter writer = new StreamWriter(_path_logs, true))
                {
                    writer.WriteLine(timedMsg);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Writing to log file failed: " + ex.Message, "FastLoading");
            }
        }

        private void UpdateStatus(string text, Brush color)
        {
            this.tbStatus.Background = color;
            this.tbStatus.Text = text;
        }

        private void Numeric_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = IsNumericInput(e.Text);
        }

        private void Numeric_PastingHandler(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                string text = (string)e.DataObject.GetData(typeof(string));
                if (IsNumericInput(text)) e.CancelCommand();
            }
            else e.CancelCommand();
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri));
            e.Handled = true;
        }

        /* 
         * Thevayl's mod
         */
        private void CbFastLoading_Check_Handler(object sender, RoutedEventArgs e)
        {
            if (this.cbFastLoading.IsEnabled == true)
                PatchFastLoading();
        }
        private bool PatchFastLoading(bool showStatus = true)
        {
            if (!this.cbFastLoading.IsEnabled || _offset_is_in_loading == 0x0 || !CanPatchGame())
            {
                return false;
            }

            float previous_deltaTime = (1000f/60)/1000f;
            if (this.cbFastLoading.IsChecked == true)
            {
                // Create and start a thread to check for loading screen
                FLST_thread = Task.Run(() =>
                {
                    try
                    {
                        bool patched = false;

                        while (!FLST_token.IsCancellationRequested)
                        {

                            bool isLoading = Read<int>(_gameAccessHwndStatic, _offset_is_in_loading).Equals(GameData.IS_IN_LOADING_TRUE);
                            //LogToFile("Thread FastLoading\n\tLoading:"+isLoading + "\n\t_offset_is_in_loading="+ _offset_is_in_loading.ToString("X") + "\n\tvalue:" + Read<int>(_gameAccessHwndStatic, _offset_is_in_loading));
                            if (isLoading && !patched)
                            {
                                patched = true;
                                float deltaTime = 1f / 1000f;
                                float speedFix = GameData.FindSpeedFixForRefreshRate(1000);
                                Debug.WriteLine("Deltatime hex: " + GetHexRepresentationFromFloat(deltaTime));
                                Debug.WriteLine("Speed hex: " + GetHexRepresentationFromFloat(speedFix));

                                previous_deltaTime = Read<float>(_gameAccessHwndStatic, _offset_framelock);
                                Debug.WriteLine("previous_deltaTime: " + previous_deltaTime + " Hex=" + GetHexRepresentationFromFloat(previous_deltaTime));
                                WriteBytes(_gameAccessHwndStatic, _offset_framelock, BitConverter.GetBytes(deltaTime));
                                _memoryCaveGenerator.UpdateDataCaveValueByName(_DATACAVE_SPEEDFIX_POINTER, BitConverter.GetBytes(speedFix));
                                _memoryCaveGenerator.ActivateDataCaveByName(_DATACAVE_SPEEDFIX_POINTER);
                            }
                            if (!isLoading && patched)
                            {
                                patched = false;
                                WriteBytes(_gameAccessHwndStatic, _offset_framelock, BitConverter.GetBytes(previous_deltaTime));
                                _memoryCaveGenerator.DeactivateDataCaveByName(_DATACAVE_SPEEDFIX_POINTER);
                            }
                            Thread.Sleep(100); // wait
                        }
                    }
                    catch (ThreadInterruptedException)
                    {
                        LogToFile("Thread FastLoading interrupted!");
                        Console.WriteLine("Thread FastLoading interrupted!");
                    }
                });

            }
            else if (this.cbFastLoading.IsChecked == false)
            {
                // Request cancellation
                if (FLST_thread != null)
                {
                    FLST_cts.Cancel();
                    FLST_cts = new CancellationTokenSource();
                    FLST_token = FLST_cts.Token;
                    FLST_thread = null;
                }
            }

            if (showStatus) UpdateStatus(DateTime.Now.ToString("HH:mm:ss") + " Game patched!", Brushes.Green);
            return true;
        }

        #region WINAPI

        private const int MOD_CONTROL = 0x0002;
        private const uint VK_M = 0x004D;
        private const uint VK_P = 0x0050;
        private const uint PROCESS_ALL_ACCESS = 0x001F0FFF;
        private const uint SPI_GETHIGHCONTRAST = 0x0042;
        private const int HCF_HIGHCONTRASTON = 0x00000001;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            UInt32 dwDesiredAccess,
            Boolean bInheritHandle,
            UInt32 dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern Boolean CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct HIGHCONTRAST
        {
            public int cbSize;
            public int dwFlags;
            public IntPtr lpszDefaultScheme;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(UInt32 uiAction, UInt32 uiParam, ref HIGHCONTRAST pvParam, UInt32 fWinIni);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, Int32 nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, Int32 nIndex, Int64 dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
        public static extern IntPtr SetWindowPos(IntPtr hWnd, Int32 hWndInsertAfter, Int32 X, Int32 Y, Int32 cx, Int32 cy, UInt32 uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;        // x position of upper-left corner
            public int Top;         // y position of upper-left corner
            public int Right;       // x position of lower-right corner
            public int Bottom;      // y position of lower-right corner
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern Boolean ReadProcessMemory(
            IntPtr hProcess,
            Int64 lpBaseAddress,
            [Out] Byte[] lpBuffer,
            UInt64 dwSize,
            out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool WriteProcessMemory(
            IntPtr hProcess,
            Int64 lpBaseAddress,
            [In, Out] Byte[] lpBuffer,
            UInt64 dwSize,
            out IntPtr lpNumberOfBytesWritten);

        #endregion
    }
}
