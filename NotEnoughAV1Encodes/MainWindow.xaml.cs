﻿using System;
using System.Windows;
using MahApps.Metro.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Threading.Tasks;
using System.IO;
using System.Text;
using System.Threading;
using System.Diagnostics;
using ControlzEx.Theming;
using System.Windows.Media;
using System.Linq;
using WPFLocalizeExtension.Engine;
using NotEnoughAV1Encodes.resources.lang;
using System.Windows.Shell;

namespace NotEnoughAV1Encodes
{
    public partial class MainWindow : MetroWindow
    {
        /// <summary>Prevents Race Conditions on Startup</summary>
        private bool startupLock = true;

        /// <summary>Encoding the Queue in Parallel or not</summary>
        private bool QueueParallel;

        /// <summary>State of the Program [0 = IDLE; 1 = Encoding; 2 = Paused]</summary>
        private int ProgramState;

        private Settings settingsDB = new();
        private Video.VideoDB videoDB = new();
        
        private string uid;
        private CancellationTokenSource cancellationTokenSource;
        public VideoSettings PresetSettings = new();
        public static bool Logging { get; set; }

        public MainWindow()
        {
            InitializeComponent();
            Initialize();
            DataContext = PresetSettings;

            if (!File.Exists(Path.Combine(Global.AppData, "NEAV1E", "settings.json")))
            {
                // First Launch
                Views.FirstStartup firstStartup = new(settingsDB);
                Hide();
                firstStartup.ShowDialog();
                Show();
            }

            LocalizeDictionary.Instance.Culture = settingsDB.CultureInfo;

            var exists = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(System.Reflection.Assembly.GetEntryAssembly().Location)).Count() > 1;

            if (exists)
            {
                MessageBox.Show(LocalizedStrings.Instance["MessageAlreadyRunning"], "", MessageBoxButton.OK, MessageBoxImage.Stop);
                Process.GetCurrentProcess().Kill();
                return;
            }
        }

        private void MetroWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Cleanup Crop Preview Images
            DeleteCropPreviews();

            if (ProgramState == 0) return;

            // Ask User if ProgramState is not IDLE (0)
            MessageBoxResult result = MessageBox.Show(LocalizedStrings.Instance["CloseQuestion"], "", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.No)
            {
                e.Cancel = true;
            }
        }

        #region Startup
        private void Initialize()
        {
            resources.MediaLanguages.FillDictionary();

            // Load Worker Count
            int coreCount = 0;
            foreach (System.Management.ManagementBaseObject item in new System.Management.ManagementObjectSearcher("Select * from Win32_Processor").Get())
            {
                coreCount += int.Parse(item["NumberOfCores"].ToString());
            }
            for (int i = 1; i <= coreCount; i++) { ComboBoxWorkerCount.Items.Add(i); }
            ComboBoxWorkerCount.SelectedItem = Convert.ToInt32(coreCount * 75 / 100);
            TextBoxWorkerCount.Text = coreCount.ToString();

            // Load Settings from JSON
            try 
            {
                settingsDB = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(Path.Combine(Global.AppData, "NEAV1E", "settings.json")));

                if (settingsDB == null)
                {
                    settingsDB = new();
                    MessageBox.Show("Program Settings File under %appdata%\\NEAV1E\\settings.json corrupted.\nProgram Settings has been reset.\nPresets are not affected.","That shouldn't have happened!");
                    try
                    {
                        File.WriteAllText(Path.Combine(Global.AppData, "NEAV1E", "settings.json"), JsonConvert.SerializeObject(settingsDB, Formatting.Indented));
                    }
                    catch (Exception ex) { MessageBox.Show(ex.Message); }
                }
            } catch { }

            LoadSettings();

            // Load Queue
            if (Directory.Exists(Path.Combine(Global.AppData, "NEAV1E", "Queue")))
            {
                string[] filePaths = Directory.GetFiles(Path.Combine(Global.AppData, "NEAV1E", "Queue"), "*.json", SearchOption.TopDirectoryOnly);

                foreach (string file in filePaths)
                {
                    ListBoxQueue.Items.Add(JsonConvert.DeserializeObject<Queue.QueueElement>(File.ReadAllText(file)));
                }
            }

            LoadPresets();

            try { ComboBoxPresets.SelectedItem = settingsDB.DefaultPreset; } catch { }
            startupLock = false;

            try { ComboBoxSortQueueBy.SelectedIndex = settingsDB.SortQueueBy; } catch { }
        }

        private void LoadPresets()
        {
            // Load Presets
            if (Directory.Exists(Path.Combine(Global.AppData, "NEAV1E", "Presets")))
            {
                string[] filePaths = Directory.GetFiles(Path.Combine(Global.AppData, "NEAV1E", "Presets"), "*.json", SearchOption.TopDirectoryOnly);

                foreach (string file in filePaths)
                {
                    ComboBoxPresets.Items.Add(Path.GetFileNameWithoutExtension(file));
                }
            }
        }
        #endregion

        #region Buttons
        private void ButtonTestSettings_Click(object sender, RoutedEventArgs e)
        {
            Views.TestCustomSettings testCustomSettings = new(settingsDB.Theme, ComboBoxVideoEncoder.SelectedIndex, CheckBoxCustomVideoSettings.IsOn ? TextBoxCustomVideoSettings.Text : GenerateEncoderCommand());
            testCustomSettings.ShowDialog();
        }

        private void ToggleSwitchFilterCrop_Toggled(object sender, RoutedEventArgs e)
        {
            CreateCropPreviewsOnLoad();
        }

        private void ButtonCropAutoDetect_Click(object sender, RoutedEventArgs e)
        {
            AutoCropDetect();
        }

        private void ButtonCropPreviewForward_Click(object sender, RoutedEventArgs e)
        {
            if (videoDB.InputPath == null) return;
            int index = int.Parse(LabelCropPreview.Content.ToString().Split("/")[0]) + 1;
            if (index > 4)
                index = 1;
            LabelCropPreview.Content = index.ToString() + "/4";

            LoadCropPreview(index);
        }
        private void ButtonCropPreviewBackward_Click(object sender, RoutedEventArgs e)
        {
            if (videoDB.InputPath == null) return;
            int index = int.Parse(LabelCropPreview.Content.ToString().Split("/")[0]) - 1;
            if (index < 1)
                index = 4;
            LabelCropPreview.Content = index.ToString() + "/4";

            LoadCropPreview(index);
        }

        private void ButtonCancelEncode_Click(object sender, RoutedEventArgs e)
        {
            if (cancellationTokenSource == null) return;
            try
            {
                cancellationTokenSource.Cancel();
                ImageStartStop.Source = new BitmapImage(new Uri(@"/NotEnoughAV1Encodes;component/resources/img/start.png", UriKind.Relative));
                ButtonAddToQueue.IsEnabled = true;
                ButtonRemoveSelectedQueueItem.IsEnabled = true;
                ButtonEditSelectedItem.IsEnabled = true;
                ButtonClearQueue.IsEnabled = true;
                ComboBoxSortQueueBy.IsEnabled = true;

                // To Do: Save Queue States when Cancelling
                // Problem: Needs VideoChunks List
                // Possible Implementation:
                //        - Use VideoChunks Functions from MainStartAsync()
                //        - Save VideoChunks inside QueueElement
                //SaveQueueElementState();
            }
            catch { }
        }

        private void ButtonProgramSettings_Click(object sender, RoutedEventArgs e)
        {
            Views.ProgramSettings programSettings = new(settingsDB);
            programSettings.ShowDialog();
            settingsDB = programSettings.settingsDBTemp;

            LoadSettings();

            try
            {
                Directory.CreateDirectory(Path.Combine(Global.AppData, "NEAV1E"));
                File.WriteAllText(Path.Combine(Global.AppData, "NEAV1E", "settings.json"), JsonConvert.SerializeObject(settingsDB, Formatting.Indented));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void ButtonRemoveSelectedQueueItem_Click(object sender, RoutedEventArgs e)
        {
            DeleteQueueItems();
        }

        private void QueueMenuItemOpenOutputDir_Click(object sender, RoutedEventArgs e)
        {
            if (ListBoxQueue.SelectedItem == null) return;
            try
            {
                Queue.QueueElement tmp = (Queue.QueueElement)ListBoxQueue.SelectedItem;
                string outPath = Path.GetDirectoryName(tmp.Output);
                ProcessStartInfo startInfo = new()
                {
                    Arguments = outPath,
                    FileName = "explorer.exe"
                };

                Process.Start(startInfo);
            }
            catch { }
        }

        private void ButtonOpenSource_Click(object sender, RoutedEventArgs e)
        {
            Views.OpenSource openSource = new(settingsDB.Theme);
            openSource.ShowDialog();
            if (openSource.Quit)
            {
                if (openSource.BatchFolder)
                {
                    // Check if Presets exist
                    if(ComboBoxPresets.Items.Count == 0)
                    {
                        MessageBox.Show(LocalizedStrings.Instance["MessageCreatePresetBeforeBatch"]);
                        return;
                    }

                    // Batch Folder Input
                    Views.BatchFolderDialog batchFolderDialog = new(settingsDB.Theme, openSource.Path, settingsDB.SubfolderBatch);
                    batchFolderDialog.ShowDialog();
                    if (batchFolderDialog.Quit)
                    {
                        List<string> files =  batchFolderDialog.Files;
                        string inputPath = batchFolderDialog.Input;
                        string preset = batchFolderDialog.Preset;
                        string output = batchFolderDialog.Output;
                        int container = batchFolderDialog.Container;
                        bool presetBitdepth = batchFolderDialog.PresetBitdepth;
                        bool activatesubtitles = batchFolderDialog.ActivateSubtitles;
                        bool mirrorFolderStructure = batchFolderDialog.MirrorFolderStructure;

                        string outputContainer = "";
                        if (container == 0) outputContainer = ".mkv";
                        else if (container == 1) outputContainer = ".webm";
                        else if (container == 2) outputContainer = ".mp4";

                        const string src = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
                        try
                        {
                            foreach (string file in files)
                            {
                                // Generate a random identifier to avoid filesystem conflicts
                                StringBuilder identifier = new();
                                Random RNG = new();
                                for (int i = 0; i < 15; i++)
                                {
                                    identifier.Append(src[RNG.Next(0, src.Length)]);
                                }

                                // Load Preset
                                PresetSettings = JsonConvert.DeserializeObject<VideoSettings>(File.ReadAllText(Path.Combine(Global.AppData, "NEAV1E", "Presets", preset + ".json")));
                                DataContext = PresetSettings;

                                // Create video object
                                videoDB = new();
                                videoDB.InputPath = file;

                                // Output Video
                                string outname = PresetSettings.PresetBatchName;
                                outname = outname.Replace("{filename}", Path.GetFileNameWithoutExtension(file));
                                outname = outname.Replace("{presetname}", preset);

                                videoDB.OutputPath = Path.Combine(output, outname + outputContainer);
                                if (mirrorFolderStructure)
                                {
                                    string relativePath = Path.GetRelativePath(inputPath, Path.GetDirectoryName(file));
                                    videoDB.OutputPath = Path.Combine(output, relativePath, outname + outputContainer);
                                }

                                videoDB.OutputFileName = Path.GetFileName(videoDB.OutputPath);
                                videoDB.ParseMediaInfo(PresetSettings);

                                try { ListBoxAudioTracks.Items.Clear(); } catch { }
                                try { ListBoxAudioTracks.ItemsSource = null; } catch { }
                                try { ListBoxSubtitleTracks.Items.Clear(); } catch { }
                                try { ListBoxSubtitleTracks.ItemsSource = null; } catch { }

                                ListBoxAudioTracks.ItemsSource = videoDB.AudioTracks;
                                ListBoxSubtitleTracks.ItemsSource = videoDB.SubtitleTracks;

                                // Automatically toggle VFR Support, if source is MKV
                                if (videoDB.MIIsVFR && Path.GetExtension(videoDB.InputPath) is ".mkv" or ".MKV")
                                {
                                    CheckBoxVideoVFR.IsEnabled = true;
                                    CheckBoxVideoVFR.IsChecked = true;
                                }
                                else
                                {
                                    CheckBoxVideoVFR.IsChecked = false;
                                    CheckBoxVideoVFR.IsEnabled = false;
                                }

                                // Uses Bit-Depth of Video
                                if (!presetBitdepth)
                                {
                                    if (videoDB.MIBitDepth == "8") ComboBoxVideoBitDepth.SelectedIndex = 0;
                                    if (videoDB.MIBitDepth == "10") ComboBoxVideoBitDepth.SelectedIndex = 1;
                                    if (videoDB.MIBitDepth == "12") ComboBoxVideoBitDepth.SelectedIndex = 2;
                                }

                                // Skip Subtitles if Container is not MKV to avoid conflicts
                                bool skipSubs = container != 0;
                                if (!activatesubtitles) skipSubs = true;

                                AddToQueue(identifier.ToString(), skipSubs);
                            }
                        }
                        catch(Exception ex)
                        {
                            MessageBox.Show(ex.Message);
                        }

                        Dispatcher.BeginInvoke((Action)(() => TabControl.SelectedIndex = 7));
                    }
                }
                else if(openSource.ProjectFile)
                {
                    // Project File Input
                    try
                    {
                        videoDB = new();
                        string file = openSource.Path;
                        Queue.QueueElement queueElement = JsonConvert.DeserializeObject<Queue.QueueElement>(File.ReadAllText(file));

                        PresetSettings = queueElement.Preset;
                        DataContext = PresetSettings;
                        videoDB = queueElement.VideoDB;

                        try { ListBoxAudioTracks.Items.Clear(); } catch { }
                        try { ListBoxAudioTracks.ItemsSource = null; } catch { }
                        try { ListBoxSubtitleTracks.Items.Clear(); } catch { }
                        try { ListBoxSubtitleTracks.ItemsSource = null; } catch { }

                        ListBoxAudioTracks.ItemsSource = videoDB.AudioTracks;
                        ListBoxSubtitleTracks.ItemsSource = videoDB.SubtitleTracks;
                        LabelVideoSource.Text = videoDB.InputPath;
                        LabelVideoDestination.Text = videoDB.OutputPath;
                        LabelVideoLength.Content = videoDB.MIDuration;
                        LabelVideoResolution.Content = videoDB.MIWidth + "x" + videoDB.MIHeight;
                        LabelVideoColorFomat.Content = videoDB.MIChromaSubsampling;

                        ComboBoxChunkingMethod.SelectedIndex = queueElement.ChunkingMethod;
                        ComboBoxReencodeMethod.SelectedIndex = queueElement.ReencodeMethod;
                        CheckBoxTwoPassEncoding.IsOn = queueElement.Passes == 2;
                        TextBoxChunkLength.Text = queueElement.ChunkLength.ToString();
                        TextBoxPySceneDetectThreshold.Text = queueElement.PySceneDetectThreshold.ToString();
                    }
                    catch(Exception ex)
                    {
                        MessageBox.Show(ex.Message);
                    }
                }
                else
                {
                    SingleFileInput(openSource.Path);
                }
            }
        }

        private void SingleFileInput(string path)
        {
            // Single File Input
            videoDB = new();
            videoDB.InputPath = path;
            videoDB.ParseMediaInfo(PresetSettings);
            LabelVideoDestination.Text = LocalizedStrings.Instance["LabelVideoDestination"];

            try { ListBoxAudioTracks.Items.Clear(); } catch { }
            try { ListBoxAudioTracks.ItemsSource = null; } catch { }
            try { ListBoxSubtitleTracks.Items.Clear(); } catch { }
            try { ListBoxSubtitleTracks.ItemsSource = null; } catch { }

            ListBoxAudioTracks.ItemsSource = videoDB.AudioTracks;
            ListBoxSubtitleTracks.ItemsSource = videoDB.SubtitleTracks;
            LabelVideoSource.Text = videoDB.InputPath;
            LabelVideoLength.Content = videoDB.MIDuration;
            LabelVideoResolution.Content = videoDB.MIWidth + "x" + videoDB.MIHeight;
            LabelVideoColorFomat.Content = videoDB.MIChromaSubsampling;
            string vfr = "";
            if (videoDB.MIIsVFR)
            {
                vfr = " (VFR)";
                if (Path.GetExtension(videoDB.InputPath) is ".mkv" or ".MKV")
                {
                    CheckBoxVideoVFR.IsEnabled = true;
                    CheckBoxVideoVFR.IsChecked = true;
                }
                else
                {
                    // VFR Video only currently supported in .mkv container
                    // Reasoning is, that splitting a VFR MP4 Video to MKV Chunks will result in ffmpeg making it CFR
                    // Additionally Copying the MP4 Video to a MKV Video will result in the same behavior, leading to incorrect extracted timestamps
                    CheckBoxVideoVFR.IsChecked = false;
                    CheckBoxVideoVFR.IsEnabled = false;
                }
            }
            LabelVideoFramerate.Content = videoDB.MIFramerate + vfr;

            // Output
            if (!string.IsNullOrEmpty(settingsDB.DefaultOutPath))
            {
                string outPath = Path.Combine(settingsDB.DefaultOutPath, Path.GetFileNameWithoutExtension(videoDB.InputPath) + settingsDB.DefaultOutContainer);

                if (videoDB.InputPath == outPath)
                {
                    outPath = Path.Combine(settingsDB.DefaultOutPath, Path.GetFileNameWithoutExtension(videoDB.InputPath) + "_av1" + settingsDB.DefaultOutContainer);
                }

                videoDB.OutputPath = outPath;
                LabelVideoDestination.Text = videoDB.OutputPath;
                videoDB.OutputFileName = Path.GetFileName(videoDB.OutputPath);

                try
                {
                    if (Path.GetExtension(videoDB.OutputPath).ToLower() == ".mp4" ||
                        Path.GetExtension(videoDB.OutputPath).ToLower() == ".webm")
                    {
                        // Disable Subtitles if Output is MP4
                        foreach (Subtitle.SubtitleTracks subtitleTracks in ListBoxSubtitleTracks.Items)
                        {
                            subtitleTracks.Active = false;
                            subtitleTracks.Enabled = false;
                        }
                    }
                }
                catch { }
            }

            DeleteCropPreviews();
            CreateCropPreviewsOnLoad();
        }

        private void ButtonSetDestination_Click(object sender, RoutedEventArgs e)
        {
            string fileName = "";

            if (!string.IsNullOrEmpty(videoDB.InputPath))
            {
                fileName = videoDB.InputFileName;
            }

            SaveFileDialog saveVideoFileDialog = new()
            {
                Filter = "MKV Video|*.mkv|WebM Video|*.webm|MP4 Video|*.mp4",
                FileName = fileName
            };

            if (saveVideoFileDialog.ShowDialog() == true)
            {
                videoDB.OutputPath = saveVideoFileDialog.FileName;
                LabelVideoDestination.Text = videoDB.OutputPath;
                videoDB.OutputFileName = Path.GetFileName(videoDB.OutputPath);
                try
                {
                    if (Path.GetExtension(videoDB.OutputPath).ToLower() == ".mp4" || 
                        Path.GetExtension(videoDB.OutputPath).ToLower() == ".webm")
                    {
                        // Disable Subtitles if Output is MP4
                        foreach (Subtitle.SubtitleTracks subtitleTracks in ListBoxSubtitleTracks.Items)
                        {
                            subtitleTracks.Active = false;
                            subtitleTracks.Enabled = false;
                        }
                    }
                    else
                    {
                        foreach (Subtitle.SubtitleTracks subtitleTracks in ListBoxSubtitleTracks.Items)
                        {
                            subtitleTracks.Enabled = true;
                        }
                    }
                }
                catch { }
            }
        }

        private void ButtonStartStop_Click(object sender, RoutedEventArgs e)
        {
            if (ListBoxQueue.Items.Count == 0)
            {
                PreAddToQueue();
            }

            if (ListBoxQueue.Items.Count != 0)
            {
                if (ProgramState is 0 or 2)
                {
                    ImageStartStop.Source = new BitmapImage(new Uri(@"/NotEnoughAV1Encodes;component/resources/img/pause.png", UriKind.Relative));
                    LabelStartPauseButton.Content = LocalizedStrings.Instance["Pause"];

                    // Main Start
                    if (ProgramState is 0)
                    {
                        ButtonAddToQueue.IsEnabled = false;
                        ButtonRemoveSelectedQueueItem.IsEnabled = false;
                        ButtonEditSelectedItem.IsEnabled = false;
                        ButtonClearQueue.IsEnabled = false;
                        ComboBoxSortQueueBy.IsEnabled = false;

                        PreStart();
                    }

                    // Resume all PIDs
                    if (ProgramState is 2)
                    {
                        foreach (int pid in Global.LaunchedPIDs)
                        {
                            Resume.ResumeProcessTree(pid);
                        }
                    }

                    ProgramState = 1;
                }
                else if (ProgramState is 1)
                {
                    ProgramState = 2;
                    ImageStartStop.Source = new BitmapImage(new Uri(@"/NotEnoughAV1Encodes;component/resources/img/resume.png", UriKind.Relative));
                    LabelStartPauseButton.Content = LocalizedStrings.Instance["Resume"];

                    // Pause all PIDs
                    foreach (int pid in Global.LaunchedPIDs)
                    {
                        Suspend.SuspendProcessTree(pid);
                    }
                }
            }
            else
            {
                MessageBox.Show(LocalizedStrings.Instance["MessageQueueEmpty"], LocalizedStrings.Instance["TabItemQueue"], MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ButtonAddToQueue_Click(object sender, RoutedEventArgs e)
        {
            PreAddToQueue();
        }

        private void PreAddToQueue()
        {
            // Prevents generating a new identifier, if queue item is being edited
            if (string.IsNullOrEmpty(uid))
            {
                // Generate a random identifier to avoid filesystem conflicts
                const string src = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
                StringBuilder identifier = new();
                Random RNG = new();
                for (int i = 0; i < 15; i++)
                {
                    identifier.Append(src[RNG.Next(0, src.Length)]);
                }
                uid = identifier.ToString();
            }

            // Add Job to Queue
            AddToQueue(uid, false);

            Dispatcher.BeginInvoke((Action)(() => TabControl.SelectedIndex = 7));

            // Reset Unique Identifier
            uid = null;
        }

        private void SaveQueueElementState(Queue.QueueElement queueElement, List<string> VideoChunks)
        {
            // Save / Override Queuefile to save Progress of Chunks

            foreach (string chunkT in VideoChunks)
            {
                // Get Index
                int index = VideoChunks.IndexOf(chunkT);

                // Already Encoded Status
                bool alreadyEncoded = File.Exists(Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier, "Video", index.ToString("D6") + "_finished.log"));

                // Remove Chunk if not finished
                if (!alreadyEncoded)
                {
                    queueElement.ChunkProgress.RemoveAll(chunk => chunk.ChunkName == chunkT);
                }
            }

            try
            {
                File.WriteAllText(Path.Combine(Global.AppData, "NEAV1E", "Queue", queueElement.VideoDB.InputFileName + "_" + queueElement.UniqueIdentifier + ".json"), JsonConvert.SerializeObject(queueElement, Formatting.Indented));
            }
            catch { }

        }

        private void ButtonSavePreset_Click(object sender, RoutedEventArgs e)
        {
            Views.SavePresetDialog savePresetDialog = new(settingsDB.Theme);
            savePresetDialog.ShowDialog();
            if (savePresetDialog.Quit)
            {
                Directory.CreateDirectory(Path.Combine(Global.AppData, "NEAV1E", "Presets"));
                PresetSettings.PresetBatchName = savePresetDialog.PresetBatchName;
                PresetSettings.AudioCodecMono = savePresetDialog.AudioCodecMono;
                PresetSettings.AudioCodecStereo = savePresetDialog.AudioCodecStereo;
                PresetSettings.AudioCodecSixChannel = savePresetDialog.AudioCodecSixChannel;
                PresetSettings.AudioCodecEightChannel = savePresetDialog.AudioCodecEightChannel;
                PresetSettings.AudioBitrateMono = savePresetDialog.AudioBitrateMono;
                PresetSettings.AudioBitrateStereo = savePresetDialog.AudioBitrateStereo;
                PresetSettings.AudioBitrateSixChannel = savePresetDialog.AudioBitrateSixChannel;
                PresetSettings.AudioBitrateEightChannel = savePresetDialog.AudioBitrateEightChannel;
                File.WriteAllText(Path.Combine(Global.AppData, "NEAV1E", "Presets", savePresetDialog.PresetName + ".json"), JsonConvert.SerializeObject(PresetSettings, Formatting.Indented));
                ComboBoxPresets.Items.Clear();
                LoadPresets();
            }
        }

        private void ButtonDeletePreset_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                File.Delete(Path.Combine(Global.AppData, "NEAV1E", "Presets", ComboBoxPresets.Text + ".json"));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }

            try
            {
                ComboBoxPresets.Items.Clear();
                LoadPresets();
            }
            catch { }

        }

        private void ButtonSetPresetDefault_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                settingsDB.DefaultPreset = ComboBoxPresets.Text;
                Directory.CreateDirectory(Path.Combine(Global.AppData, "NEAV1E"));
                File.WriteAllText(Path.Combine(Global.AppData, "NEAV1E", "settings.json"), JsonConvert.SerializeObject(settingsDB, Formatting.Indented));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void ButtonEditSelectedItem_Click(object sender, RoutedEventArgs e)
        {
            if (ProgramState != 0) return;

            if (ListBoxQueue.SelectedItem != null)
            {
                if (ListBoxQueue.SelectedItems.Count == 1)
                {
                    // Editing one entry
                    Queue.QueueElement tmp = (Queue.QueueElement)ListBoxQueue.SelectedItem;
                    PresetSettings = tmp.Preset;
                    DataContext = PresetSettings;
                    videoDB = tmp.VideoDB;
                    uid = tmp.UniqueIdentifier;

                    try { ListBoxAudioTracks.Items.Clear(); } catch { }
                    try { ListBoxAudioTracks.ItemsSource = null; } catch { }
                    try { ListBoxSubtitleTracks.Items.Clear(); } catch { }
                    try { ListBoxSubtitleTracks.ItemsSource = null; } catch { }

                    ListBoxAudioTracks.ItemsSource = videoDB.AudioTracks;
                    ListBoxSubtitleTracks.ItemsSource = videoDB.SubtitleTracks;
                    LabelVideoSource.Text = videoDB.InputPath;
                    LabelVideoDestination.Text = videoDB.OutputPath;
                    LabelVideoLength.Content = videoDB.MIDuration;
                    LabelVideoResolution.Content = videoDB.MIWidth + "x" + videoDB.MIHeight;
                    LabelVideoColorFomat.Content = videoDB.MIChromaSubsampling;

                    ComboBoxChunkingMethod.SelectedIndex = tmp.ChunkingMethod;
                    ComboBoxReencodeMethod.SelectedIndex = tmp.ReencodeMethod;
                    CheckBoxTwoPassEncoding.IsOn = tmp.Passes == 2;
                    TextBoxChunkLength.Text = tmp.ChunkLength.ToString();
                    TextBoxPySceneDetectThreshold.Text = tmp.PySceneDetectThreshold.ToString();

                    try
                    {
                        File.Delete(Path.Combine(Global.AppData, "NEAV1E", "Queue", tmp.VideoDB.InputFileName + "_" + tmp.UniqueIdentifier + ".json"));
                    }
                    catch { }

                    ListBoxQueue.Items.Remove(ListBoxQueue.SelectedItem);

                    Dispatcher.BeginInvoke((Action)(() => TabControl.SelectedIndex = 0));
                }
            }
        }

        private void ButtonClearQueue_Click(object sender, RoutedEventArgs e)
        {
            if (ProgramState != 0) return;
            List<Queue.QueueElement> items = ListBoxQueue.Items.OfType<Queue.QueueElement>().ToList();
            foreach (var item in items)
            {
                ListBoxQueue.Items.Remove(item);
                try
                {
                    File.Delete(Path.Combine(Global.AppData, "NEAV1E", "Queue", item.VideoDB.InputFileName + "_" + item.UniqueIdentifier + ".json"));
                }
                catch { }
            }
        }

        private void QueueMenuItemSave_Click(object sender, RoutedEventArgs e)
        {
            if (ListBoxQueue.SelectedItem != null)
            {
                try
                {
                    Queue.QueueElement tmp = (Queue.QueueElement)ListBoxQueue.SelectedItem;
                    SaveFileDialog saveVideoFileDialog = new();
                    saveVideoFileDialog.AddExtension = true;
                    saveVideoFileDialog.Filter = "JSON File|*.json";
                    if (saveVideoFileDialog.ShowDialog() == true)
                    {
                        File.WriteAllText(saveVideoFileDialog.FileName, JsonConvert.SerializeObject(tmp, Formatting.Indented));
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                }
            }
        }

        private void ListBoxQueue_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                DeleteQueueItems();
            }
        }

        private void AudioTracksImport_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openAudioFilesDialog = new()
            {
                Filter = "Audio Files|*.mp3;*.aac;*.flac;*.m4a;*.ogg;*.opus;*.wav;*.wma|All Files|*.*",
                Multiselect = true
            };

            bool? result = openAudioFilesDialog.ShowDialog();
            if (result == true)
            {
                List<Audio.AudioTracks> AudioTracks = new();
                if (ListBoxAudioTracks.ItemsSource != null)
                {
                    AudioTracks = (List<Audio.AudioTracks>) ListBoxAudioTracks.ItemsSource;
                }
                foreach (string file in openAudioFilesDialog.FileNames)
                {
                    Debug.WriteLine(file);
                    AudioTracks.Add(videoDB.ParseMediaInfoAudio(file, PresetSettings));
                }

                try { ListBoxAudioTracks.Items.Clear(); } catch { }
                try { ListBoxAudioTracks.ItemsSource = null; } catch { }
                try { ListBoxSubtitleTracks.Items.Clear(); } catch { }
                try { ListBoxSubtitleTracks.ItemsSource = null; } catch { }

                videoDB.AudioTracks = AudioTracks;
                ListBoxAudioTracks.ItemsSource = AudioTracks;
            }
        }
        #endregion

        #region UI Functions

        private void MetroWindow_Drop(object sender, DragEventArgs e)
        {
            // Drag & Drop Video Files into GUI
            List<string> filepaths = new();
            foreach (var s in (string[])e.Data.GetData(DataFormats.FileDrop, false)) { filepaths.Add(s); }
            int counter = 0;
            foreach (var item in filepaths) { counter += 1; }
            foreach (var item in filepaths)
            {
                if (counter == 1)
                {
                    // Single File Input
                    SingleFileInput(item);
                }
            }
            if (counter > 1)
            {
                MessageBox.Show("Please use Batch Input (Drag & Drop multiple Files is not supported)");
            }
        }
        private bool presetLoadLock = false;
        private void ComboBoxPresets_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ComboBoxPresets.SelectedItem == null) return;
            try
            {
                presetLoadLock = true;
                PresetSettings = JsonConvert.DeserializeObject<VideoSettings>(File.ReadAllText(Path.Combine(Global.AppData, "NEAV1E", "Presets", ComboBoxPresets.SelectedItem.ToString() + ".json")));
                DataContext = PresetSettings;
                presetLoadLock = false;

                ApplyPresetAudioToCurrentVideo();
            }
            catch { }
        }

        private void ApplyPresetAudioToCurrentVideo()
        {
            try
            {
                if (ListBoxAudioTracks.ItemsSource == null) return;
                videoDB.AudioTracks = (List<Audio.AudioTracks>)ListBoxAudioTracks.ItemsSource;
                try { ListBoxAudioTracks.Items.Clear(); } catch { }
                try { ListBoxAudioTracks.ItemsSource = null; } catch { }

                foreach (Audio.AudioTracks audioTrack in videoDB.AudioTracks)
                {
                    switch (audioTrack.Channels)
                    {
                        case 0:
                            audioTrack.Bitrate = PresetSettings.AudioBitrateMono.ToString();
                            audioTrack.Codec = PresetSettings.AudioCodecMono;
                            break;
                        case 1:
                            audioTrack.Bitrate = PresetSettings.AudioBitrateStereo.ToString();
                            audioTrack.Codec = PresetSettings.AudioCodecStereo;
                            break;
                        case 2:
                            audioTrack.Bitrate = PresetSettings.AudioBitrateSixChannel.ToString();
                            audioTrack.Codec = PresetSettings.AudioCodecSixChannel;
                            break;
                        case 3:
                            audioTrack.Bitrate = PresetSettings.AudioBitrateEightChannel.ToString();
                            audioTrack.Codec = PresetSettings.AudioCodecEightChannel;
                            break;
                        default:
                            break;
                    }
                }

                ListBoxAudioTracks.ItemsSource = videoDB.AudioTracks;
            }
            catch { }
        }

        private void ComboBoxVideoEncoder_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (SliderEncoderPreset == null) return;

            if (ComboBoxVideoEncoder.SelectedIndex is (int) Video.Encoder.AOMFFMPEG or (int) Video.Encoder.AOMENC)
            {
                //aom ffmpeg
                if (ComboBoxVideoEncoder.SelectedIndex == 0)
                {
                    SliderEncoderPreset.Maximum = 8;
                }
                else
                {
                    SliderEncoderPreset.Maximum = 9;
                }
                SliderEncoderPreset.Value = 4;
                CheckBoxTwoPassEncoding.IsEnabled = true;
                ComboBoxVideoBitDepth.Visibility = Visibility.Visible;
                ComboBoxVideoBitDepthLimited.Visibility = Visibility.Collapsed;
            }
            else if (ComboBoxVideoEncoder.SelectedIndex is (int) Video.Encoder.RAV1EFFMPEG or (int) Video.Encoder.RAV1E)
            {
                //rav1e ffmpeg
                ComboBoxQualityMode.SelectedIndex = 0;
                SliderEncoderPreset.Maximum = 10;
                SliderEncoderPreset.Value = 5;
                CheckBoxTwoPassEncoding.IsOn = false;
                CheckBoxTwoPassEncoding.IsEnabled = false;
                CheckBoxRealTimeMode.IsOn = false;
                CheckBoxRealTimeMode.Visibility = Visibility.Collapsed;
                ComboBoxVideoBitDepth.Visibility = Visibility.Visible;
                ComboBoxVideoBitDepthLimited.Visibility = Visibility.Collapsed;
            }
            else if (ComboBoxVideoEncoder.SelectedIndex is (int) Video.Encoder.SVTAV1FFMPEG or (int) Video.Encoder.SVTAV1)
            {
                //svt-av1 ffmpeg
                ComboBoxQualityMode.SelectedIndex = 0;
                SliderEncoderPreset.Maximum = 13;
                SliderEncoderPreset.Value = 10;
                CheckBoxTwoPassEncoding.IsEnabled = true;
                CheckBoxTwoPassEncoding.IsOn = false;
                CheckBoxRealTimeMode.IsOn = false;
                CheckBoxRealTimeMode.Visibility = Visibility.Collapsed;
                ComboBoxVideoBitDepth.Visibility = Visibility.Visible;
                ComboBoxVideoBitDepthLimited.Visibility = Visibility.Collapsed;
            }
            else if (ComboBoxVideoEncoder.SelectedIndex is (int) Video.Encoder.VPXVP9FFMPEG)
            {
                //vpx-vp9 ffmpeg
                SliderEncoderPreset.Maximum = 8;
                SliderEncoderPreset.Value = 4;
                CheckBoxTwoPassEncoding.IsEnabled = true;
                CheckBoxRealTimeMode.IsOn = false;
                CheckBoxRealTimeMode.Visibility = Visibility.Collapsed;
                ComboBoxVideoBitDepth.Visibility = Visibility.Visible;
                ComboBoxVideoBitDepthLimited.Visibility = Visibility.Collapsed;
            }
            else if (ComboBoxVideoEncoder.SelectedIndex is (int) Video.Encoder.X265 or (int) Video.Encoder.X264)
            {
                //libx265 libx264 ffmpeg
                SliderEncoderPreset.Maximum = 9;
                SliderEncoderPreset.Value = 4;
                CheckBoxTwoPassEncoding.IsEnabled = false;
                CheckBoxTwoPassEncoding.IsOn = false;
                CheckBoxRealTimeMode.IsOn = false;
                CheckBoxRealTimeMode.Visibility = Visibility.Collapsed;
                ComboBoxVideoBitDepth.Visibility = Visibility.Visible;
                ComboBoxVideoBitDepthLimited.Visibility = Visibility.Collapsed;
            }
            else if (ComboBoxVideoEncoder.SelectedIndex is (int) Video.Encoder.QSVAV1)
            {
                // av1 hardware (intel arc)
                SliderEncoderPreset.Maximum = 6;
                SliderEncoderPreset.Value = 3;
                CheckBoxTwoPassEncoding.IsEnabled = false;
                CheckBoxTwoPassEncoding.IsOn = false;
                CheckBoxRealTimeMode.IsOn = false;
                CheckBoxRealTimeMode.Visibility = Visibility.Collapsed;
                ComboBoxVideoBitDepth.Visibility = Visibility.Collapsed;
                ComboBoxVideoBitDepthLimited.Visibility = Visibility.Visible;
            }
            else if (ComboBoxVideoEncoder.SelectedIndex is (int) Video.Encoder.NVENCAV1)
            {
                // av1 hardware (nvenc rtx 4000)
                SliderEncoderPreset.Maximum = 2;
                SliderEncoderPreset.Value = 1;
                CheckBoxTwoPassEncoding.IsEnabled = false;
                CheckBoxTwoPassEncoding.IsOn = false;
                CheckBoxRealTimeMode.IsOn = false;
                CheckBoxRealTimeMode.Visibility = Visibility.Collapsed;
                ComboBoxVideoBitDepth.Visibility = Visibility.Collapsed;
                ComboBoxVideoBitDepthLimited.Visibility = Visibility.Visible;
            }
            if (ComboBoxVideoEncoder.SelectedIndex is (int) Video.Encoder.X264)
            {
                if (ComboBoxQualityMode.SelectedIndex == 2)
                {
                    CheckBoxTwoPassEncoding.IsEnabled = true;
                }

                ComboBoxVideoBitDepth.Visibility = Visibility.Collapsed;
                ComboBoxVideoBitDepthLimited.Visibility = Visibility.Visible;
            }
        }

        private void ComboBoxQualityMode_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Hide all
            LabelQuantizer.Visibility = Visibility.Collapsed;
            SliderQualityAOMFFMPEG.Visibility = Visibility.Collapsed;
            LabelQuantizerPreview.Visibility = Visibility.Collapsed;
            LabelBitrateMin.Visibility = Visibility.Collapsed;
            TextBoxMinBitrateAOMFFMPEG.Visibility = Visibility.Collapsed;
            LabelBitrateAvg.Visibility = Visibility.Collapsed;
            TextBoxAVGBitrateAOMFFMPEG.Visibility = Visibility.Collapsed;
            LabelBitrateMax.Visibility = Visibility.Collapsed;
            TextBoxMaxBitrateAOMFFMPEG.Visibility = Visibility.Collapsed;
            LabelTargetVMAF.Visibility = Visibility.Collapsed;
            LabelTargetVMAFPreview.Visibility = Visibility.Collapsed;
            SliderTargetVMAF.Visibility = Visibility.Collapsed;
            LabelTargetVMAFProbes.Visibility = Visibility.Collapsed;
            SliderTargetVMAFProbes.Visibility = Visibility.Collapsed;
            LabelTargetVMAFProbesPreview.Visibility = Visibility.Collapsed;
            LabelTargetVMAFMinQ.Visibility = Visibility.Collapsed;
            SliderTargetVMAFMinQ.Visibility= Visibility.Collapsed;
            LabelTargetVMAFMinQPreview.Visibility = Visibility.Collapsed;
            LabelTargetVMAFMaxQ.Visibility = Visibility.Collapsed;
            SliderTargetVMAFMaxQ.Visibility = Visibility.Collapsed;
            LabelTargetVMAFMaxQPreview.Visibility = Visibility.Collapsed;
            LabelTargetVMAFMaxProbeLength.Visibility = Visibility.Collapsed;
            SliderTargetVMAFMaxProbeLength.Visibility = Visibility.Collapsed;
            LabelTargetVMAFMaxProbeLengthPreview.Visibility = Visibility.Collapsed;
            PresetSettings.TargetVMAF = false;

            if (ComboBoxQualityMode.SelectedIndex == 0)
            {
                // Constant Quality
                LabelQuantizer.Visibility = Visibility.Visible;
                SliderQualityAOMFFMPEG.Visibility = Visibility.Visible;
                LabelQuantizerPreview.Visibility = Visibility.Visible;
            }
            else if (ComboBoxQualityMode.SelectedIndex == 1)
            {
                // Constrained Quality
                TextBoxMaxBitrateAOMFFMPEG.Visibility = Visibility.Visible;
                LabelBitrateMax.Visibility = Visibility.Visible;
                LabelQuantizer.Visibility = Visibility.Visible;
                SliderQualityAOMFFMPEG.Visibility = Visibility.Visible;
                LabelQuantizerPreview.Visibility = Visibility.Visible;
            }
            else if (ComboBoxQualityMode.SelectedIndex == 2)
            {
                // Average Bitrate
                LabelBitrateAvg.Visibility = Visibility.Visible;
                TextBoxAVGBitrateAOMFFMPEG.Visibility = Visibility.Visible;
            }
            else if (ComboBoxQualityMode.SelectedIndex == 3)
            {
                // Constrained Bitrate
                LabelBitrateMin.Visibility = Visibility.Visible;
                TextBoxMinBitrateAOMFFMPEG.Visibility = Visibility.Visible;
                LabelBitrateAvg.Visibility = Visibility.Visible;
                TextBoxAVGBitrateAOMFFMPEG.Visibility = Visibility.Visible;
                LabelBitrateMax.Visibility = Visibility.Visible;
                TextBoxMaxBitrateAOMFFMPEG.Visibility = Visibility.Visible;
            }
            else if (ComboBoxQualityMode.SelectedIndex == 4) 
            {
                // Target VMAF
                PresetSettings.TargetVMAF = true;
                LabelTargetVMAF.Visibility = Visibility.Visible;
                LabelTargetVMAFPreview.Visibility = Visibility.Visible;
                SliderTargetVMAF.Visibility = Visibility.Visible;
                LabelTargetVMAFProbes.Visibility = Visibility.Visible;
                SliderTargetVMAFProbes.Visibility = Visibility.Visible;
                LabelTargetVMAFProbesPreview.Visibility = Visibility.Visible;
                LabelTargetVMAFMinQ.Visibility = Visibility.Visible;
                SliderTargetVMAFMinQ.Visibility = Visibility.Visible;
                LabelTargetVMAFMinQPreview.Visibility = Visibility.Visible;
                LabelTargetVMAFMaxQ.Visibility = Visibility.Visible;
                SliderTargetVMAFMaxQ.Visibility = Visibility.Visible;
                LabelTargetVMAFMaxQPreview.Visibility = Visibility.Visible;
                LabelTargetVMAFMaxProbeLength.Visibility = Visibility.Visible;
                SliderTargetVMAFMaxProbeLength.Visibility = Visibility.Visible;
                LabelTargetVMAFMaxProbeLengthPreview.Visibility = Visibility.Visible;
            }
        }

        private void SliderTargetVMAFMinQ_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SliderTargetVMAF == null) return;
            if (SliderTargetVMAFMinQ.Value > SliderTargetVMAFMaxQ.Value)
            {
                SliderTargetVMAFMaxQ.Value = SliderTargetVMAFMinQ.Value;
            }
        }

        private void SliderTargetVMAFMaxQ_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SliderTargetVMAF == null) return;
            if (SliderTargetVMAFMinQ.Value > SliderTargetVMAFMaxQ.Value)
            {
                SliderTargetVMAFMinQ.Value = SliderTargetVMAFMaxQ.Value;
            }
        }

        private void ComboBoxQualityModeRAV1EFFMPEG_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ComboBoxQualityModeRAV1EFFMPEG.SelectedIndex == 0)
            {
                SliderQualityRAV1EFFMPEG.IsEnabled = true;
                TextBoxBitrateRAV1EFFMPEG.IsEnabled = false;
            }
            else if (ComboBoxQualityModeRAV1EFFMPEG.SelectedIndex == 1)
            {
                SliderQualityRAV1EFFMPEG.IsEnabled = false;
                TextBoxBitrateRAV1EFFMPEG.IsEnabled = true;
            }
        }

        private void ComboBoxQualityModeSVTAV1FFMPEG_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ComboBoxQualityModeSVTAV1FFMPEG.SelectedIndex == 0)
            {
                SliderQualitySVTAV1FFMPEG.IsEnabled = true;
                TextBoxBitrateSVTAV1FFMPEG.IsEnabled = false;
            }
            else if (ComboBoxQualityModeSVTAV1FFMPEG.SelectedIndex == 1)
            {
                SliderQualitySVTAV1FFMPEG.IsEnabled = false;
                TextBoxBitrateSVTAV1FFMPEG.IsEnabled = true;
            }
        }

        private void ComboBoxQualityModeVP9FFMPEG_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ComboBoxQualityModeVP9FFMPEG.SelectedIndex == 0)
            {
                SliderQualityVP9FFMPEG.IsEnabled = true;
                TextBoxAVGBitrateVP9FFMPEG.IsEnabled = false;
                TextBoxMaxBitrateVP9FFMPEG.IsEnabled = false;
                TextBoxMinBitrateVP9FFMPEG.IsEnabled = false;
            }
            else if (ComboBoxQualityModeVP9FFMPEG.SelectedIndex == 1)
            {
                SliderQualityVP9FFMPEG.IsEnabled = true;
                TextBoxAVGBitrateVP9FFMPEG.IsEnabled = false;
                TextBoxMaxBitrateVP9FFMPEG.IsEnabled = true;
                TextBoxMinBitrateVP9FFMPEG.IsEnabled = false;
            }
            else if (ComboBoxQualityModeVP9FFMPEG.SelectedIndex == 2)
            {
                SliderQualityVP9FFMPEG.IsEnabled = false;
                TextBoxAVGBitrateVP9FFMPEG.IsEnabled = true;
                TextBoxMaxBitrateVP9FFMPEG.IsEnabled = false;
                TextBoxMinBitrateVP9FFMPEG.IsEnabled = false;
            }
            else if (ComboBoxQualityModeVP9FFMPEG.SelectedIndex == 3)
            {
                SliderQualityVP9FFMPEG.IsEnabled = false;
                TextBoxAVGBitrateVP9FFMPEG.IsEnabled = true;
                TextBoxMaxBitrateVP9FFMPEG.IsEnabled = true;
                TextBoxMinBitrateVP9FFMPEG.IsEnabled = true;
            }
        }

        private void ComboBoxQualityModeAOMENC_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ComboBoxQualityModeAOMENC.SelectedIndex == 0)
            {
                SliderQualityAOMENC.IsEnabled = true;
                TextBoxBitrateAOMENC.IsEnabled = false;
            }
            else if (ComboBoxQualityModeAOMENC.SelectedIndex == 1)
            {
                SliderQualityAOMENC.IsEnabled = true;
                TextBoxBitrateAOMENC.IsEnabled = true;
            }
            else if (ComboBoxQualityModeAOMENC.SelectedIndex == 2)
            {
                SliderQualityAOMENC.IsEnabled = false;
                TextBoxBitrateAOMENC.IsEnabled = true;
            }
            else if (ComboBoxQualityModeAOMENC.SelectedIndex == 3)
            {
                SliderQualityAOMENC.IsEnabled = false;
                TextBoxBitrateAOMENC.IsEnabled = true;
            }
        }

        private void ComboBoxQualityModeRAV1E_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ComboBoxQualityModeRAV1E.SelectedIndex == 0)
            {
                SliderQualityRAV1E.IsEnabled = true;
                TextBoxBitrateRAV1E.IsEnabled = false;
            }
            else if (ComboBoxQualityModeRAV1E.SelectedIndex == 1)
            {
                SliderQualityRAV1E.IsEnabled = false;
                TextBoxBitrateRAV1E.IsEnabled = true;
            }
        }

        private void ComboBoxQualityModeSVTAV1_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ComboBoxQualityModeSVTAV1.SelectedIndex == 0)
            {
                SliderQualitySVTAV1.IsEnabled = true;
                TextBoxBitrateSVTAV1.IsEnabled = false;
            }
            else if (ComboBoxQualityModeSVTAV1.SelectedIndex == 1)
            {
                SliderQualitySVTAV1.IsEnabled = false;
                TextBoxBitrateSVTAV1.IsEnabled = true;
            }
        }

        private void ComboBoxQualityModeX26x_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ComboBoxQualityModeX26x.SelectedIndex == 0)
            {
                SliderQualityX26x.IsEnabled = true;
                TextBoxBitrateX26x.IsEnabled = false;
            }
            else if (ComboBoxQualityModeX26x.SelectedIndex == 1)
            {
                SliderQualityX26x.IsEnabled = false;
                TextBoxBitrateX26x.IsEnabled = true;
            }
            if (ComboBoxVideoEncoder.SelectedIndex is (int) Video.Encoder.X264 && ComboBoxQualityModeX26x.SelectedIndex == 1)
            {
                CheckBoxTwoPassEncoding.IsEnabled = true;
            }
            else if (ComboBoxVideoEncoder.SelectedIndex is (int) Video.Encoder.X264 && ComboBoxQualityModeX26x.SelectedIndex != 1)
            {
                CheckBoxTwoPassEncoding.IsEnabled = false;
            }
        }

        private void ComboBoxQualityModeQSVAV1_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ComboBoxQualityModeQSVAV1.SelectedIndex == 0)
            {
                SliderQualityQSVAV1.IsEnabled = true;
                TextBoxBitrateQSVAV1.IsEnabled = false;
            }
            else if (ComboBoxQualityModeQSVAV1.SelectedIndex == 1)
            {
                SliderQualityQSVAV1.IsEnabled = true;
                TextBoxBitrateQSVAV1.IsEnabled = false;
            }
            else if (ComboBoxQualityModeQSVAV1.SelectedIndex == 2)
            {
                SliderQualityQSVAV1.IsEnabled = false;
                TextBoxBitrateQSVAV1.IsEnabled = true;
            }
            else if (ComboBoxQualityModeQSVAV1.SelectedIndex == 3)
            {
                SliderQualityQSVAV1.IsEnabled = false;
                TextBoxBitrateQSVAV1.IsEnabled = true;
            }
        }

        private void ComboBoxQualityModeNVENCAV1_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ComboBoxQualityModeNVENCAV1.SelectedIndex == 0)
            {
                SliderQualityNVENCAV1.IsEnabled = true;
                TextBoxBitrateNVENCAV1.IsEnabled = false;
            }
            else if (ComboBoxQualityModeNVENCAV1.SelectedIndex == 1)
            {
                SliderQualityNVENCAV1.IsEnabled = false;
                TextBoxBitrateNVENCAV1.IsEnabled = true;
            }
            else if (ComboBoxQualityModeNVENCAV1.SelectedIndex == 2)
            {
                SliderQualityNVENCAV1.IsEnabled = false;
                TextBoxBitrateNVENCAV1.IsEnabled = true;
            }
        }

        private void CheckBoxTwoPassEncoding_Checked(object sender, RoutedEventArgs e)
        {
            if (ComboBoxVideoEncoder.SelectedIndex == (int) Video.Encoder.SVTAV1 && ComboBoxQualityModeSVTAV1.SelectedIndex == 0 && CheckBoxTwoPassEncoding.IsOn)
            {
                CheckBoxTwoPassEncoding.IsOn = false;
            }

            if (ComboBoxVideoEncoder.SelectedIndex == (int) Video.Encoder.SVTAV1FFMPEG && ComboBoxQualityModeSVTAV1FFMPEG.SelectedIndex == 0 && CheckBoxTwoPassEncoding.IsOn)
            {
                CheckBoxTwoPassEncoding.IsOn = false;
            }

            if (CheckBoxRealTimeMode.IsOn && CheckBoxTwoPassEncoding.IsOn)
            {
                CheckBoxTwoPassEncoding.IsOn = false;
            }
        }

        private void CheckBoxRealTimeMode_Toggled(object sender, RoutedEventArgs e)
        {
            // Reverts to 1 Pass encoding if Real Time Mode is activated
            if (CheckBoxRealTimeMode.IsOn && CheckBoxTwoPassEncoding.IsOn)
            {
                CheckBoxTwoPassEncoding.IsOn = false;
            }
        }

        private void SliderEncoderPreset_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Shows / Hides Real Time Mode CheckBox
            if (CheckBoxRealTimeMode != null && ComboBoxVideoEncoder != null)
            {
                if (ComboBoxVideoEncoder.SelectedIndex == (int) Video.Encoder.AOMFFMPEG || ComboBoxVideoEncoder.SelectedIndex == (int) Video.Encoder.AOMENC)
                {
                    if (SliderEncoderPreset.Value >= 5)
                    {
                        CheckBoxRealTimeMode.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        CheckBoxRealTimeMode.IsOn = false;
                        CheckBoxRealTimeMode.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    CheckBoxRealTimeMode.Visibility = Visibility.Collapsed;
                }
            }

            // x264 / x265
            if (ComboBoxVideoEncoder.SelectedIndex is (int) Video.Encoder.X265 or (int) Video.Encoder.X264)
            {
                LabelSpeedValue.Content = GenerateMPEGEncoderSpeed();
            }

            // av1 hardware (Intel Arc)
            if (ComboBoxVideoEncoder.SelectedIndex is (int) Video.Encoder.QSVAV1)
            {
                LabelSpeedValue.Content = GenerateQuickSyncEncoderSpeed();
            }

            // av1 hardware (nvenc rtx 4000)
            if (ComboBoxVideoEncoder.SelectedIndex is (int)Video.Encoder.NVENCAV1)
            {
                LabelSpeedValue.Content = GenerateNVENCEncoderSpeed();
            }
        }

        private void CheckBoxCustomVideoSettings_Toggled(object sender, RoutedEventArgs e)
        {
            if (CheckBoxCustomVideoSettings.IsOn && presetLoadLock == false && IsLoaded)
            {
                TextBoxCustomVideoSettings.Text = GenerateEncoderCommand();
            }
        }

        private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
        {
            // Validates that the TextBox Input are only numbers
            Regex regex = new("[^0-9]+");
            e.Handled = regex.IsMatch(e.Text);
        }

        private void TextBoxCustomVideoSettings_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // Verifies the arguments the user inputs into the encoding settings textbox
            // If the users writes a "forbidden" argument, it will display the text red
            string[] forbiddenWords = { "help", "cfg", "debug", "output", "passes", "pass", "fpf", "limit",
            "skip", "webm", "ivf", "obu", "q-hist", "rate-hist", "fullhelp", "benchmark", "first-pass", "second-pass",
            "reconstruction", "enc-mode-2p", "input-stat-file", "output-stat-file" };

            foreach (string word in forbiddenWords)
            {
                if (settingsDB.BaseTheme == 0)
                {
                    // Lightmode
                    TextBoxCustomVideoSettings.Foreground = new SolidColorBrush(Color.FromRgb(0, 0, 0));
                }
                else
                {
                    // Darkmode
                    TextBoxCustomVideoSettings.Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                }

                if (TextBoxCustomVideoSettings.Text.Contains(word))
                {
                    TextBoxCustomVideoSettings.Foreground = new SolidColorBrush(Color.FromRgb(255, 0, 0));
                    break;
                }
            }
        }

        bool lockQueue = false;
        private void ComboBoxSortQueueBy_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (startupLock) return;
            if (lockQueue) return;
            if (ProgramState != 0) return;
            settingsDB.SortQueueBy = ComboBoxSortQueueBy.SelectedIndex;
            try
            {
                Directory.CreateDirectory(Path.Combine(Global.AppData, "NEAV1E"));
                File.WriteAllText(Path.Combine(Global.AppData, "NEAV1E", "settings.json"), JsonConvert.SerializeObject(settingsDB, Formatting.Indented));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }

            SortQueue();
        }
        #endregion

        #region Small Functions

        private void DeleteCropPreviews()
        {
            for (int i = 1; i < 5; i++)
            {
                string image = Path.Combine(Global.Temp, "NEAV1E", "crop_preview_" + i.ToString() + ".bmp");
                if (File.Exists(image))
                {
                    try
                    {
                        File.Delete(image);
                    }
                    catch { }
                }
            }
        }

        private async void CreateCropPreviewsOnLoad()
        {
            if (!IsLoaded) return;

            if (videoDB.InputPath == null) return;

            if (!ToggleSwitchFilterCrop.IsOn)
            {
                ImageCropPreview.Source = new BitmapImage(new Uri("pack://application:,,,/NotEnoughAV1Encodes;component/resources/img/videoplaceholder.jpg")); ;
                return;
            }

            string crop = "-vf " + VideoFiltersCrop();

            await Task.Run(() => CreateCropPreviews(crop));

            try
            {
                int index = int.Parse(LabelCropPreview.Content.ToString().Split("/")[0]);

                MemoryStream memStream = new(File.ReadAllBytes(Path.Combine(Global.Temp, "NEAV1E", "crop_preview_" + index.ToString() + ".bmp")));
                BitmapImage bmi = new();
                bmi.BeginInit();
                bmi.StreamSource = memStream;
                bmi.EndInit();
                ImageCropPreview.Source = bmi;
            }
            catch { }
        }

        private async void AutoCropDetect()
        {
            if (videoDB.InputPath == null) return;

            List<string> cropList = new();

            string time = videoDB.MIDuration;
            
            int seconds = Convert.ToInt32(Math.Floor(TimeSpan.Parse(time).TotalSeconds / 4));

            // Use the current frame as start point of detection
            int index = int.Parse(LabelCropPreview.Content.ToString().Split("/")[0]);

            string command = "/C ffmpeg.exe -ss " + (index * seconds).ToString() + " -i \"" + videoDB.InputPath + "\" -vf cropdetect=24:16:0 -t 5  -f null -";

            Process ffmpegProcess = new();
            ProcessStartInfo startInfo = new()
            {
                FileName = "cmd.exe",
                WorkingDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Apps", "FFmpeg"),
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = command,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };

            ffmpegProcess.StartInfo = startInfo;
            ffmpegProcess.Start();

            string lastLine;
            while (! ffmpegProcess.StandardError.EndOfStream)
            {
                lastLine = ffmpegProcess.StandardError.ReadLine();
                if (lastLine.Contains("crop="))
                {
                    cropList.Add(lastLine.Split("crop=")[1]);
                }
            }

            ffmpegProcess.WaitForExit();

            // Get most occuring value
            string crop = cropList.Where(c => !string.IsNullOrEmpty(c)).GroupBy(a => a).OrderByDescending(b => b.Key[1].ToString()).First().Key;

            try
            {
                // Translate Output to crop values
                int cropTop = int.Parse(crop.Split(":")[3]);
                TextBoxFiltersCropTop.Text = cropTop.ToString();

                int cropLeft = int.Parse(crop.Split(":")[2]);
                TextBoxFiltersCropLeft.Text = cropLeft.ToString();

                int cropBottom = videoDB.MIHeight - cropTop - int.Parse(crop.Split(":")[1]);
                TextBoxFiltersCropBottom.Text = cropBottom.ToString();

                int cropRight = videoDB.MIWidth - cropLeft - int.Parse(crop.Split(":")[0]);
                TextBoxFiltersCropRight.Text = cropRight.ToString();

                string cropNew = "-vf " + VideoFiltersCrop();
                await Task.Run(() => CreateCropPreviews(cropNew));
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void CreateCropPreviews(string crop)
        {
            Directory.CreateDirectory(Path.Combine(Global.Temp, "NEAV1E"));

            string time = videoDB.MIDuration;
            int seconds = Convert.ToInt32(Math.Floor(TimeSpan.Parse(time).TotalSeconds / 4));

            for (int i = 1; i < 5; i++)
            {
                // Extract Frames
                string command = "/C ffmpeg.exe -y -ss " + (i * seconds).ToString() + " -i \"" + videoDB.InputPath + "\" -vframes 1 " + crop + " \"" + Path.Combine(Global.Temp, "NEAV1E", "crop_preview_" + i.ToString() + ".bmp") + "\"";

                Process ffmpegProcess = new();
                ProcessStartInfo startInfo = new()
                {
                    UseShellExecute = true,
                    FileName = "cmd.exe",
                    WorkingDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Apps", "FFmpeg"),
                    WindowStyle = ProcessWindowStyle.Hidden,
                    Arguments = command
                };

                ffmpegProcess.StartInfo = startInfo;
                ffmpegProcess.Start();
                ffmpegProcess.WaitForExit();
            }
        }

        private void LoadCropPreview(int index)
        {
            string input = Path.Combine(Global.Temp, "NEAV1E", "crop_preview_" + index.ToString() + ".bmp");
            if (! File.Exists(input)) return;

            try
            {
                MemoryStream memStream = new(File.ReadAllBytes(input));
                BitmapImage bmi = new();
                bmi.BeginInit();
                bmi.StreamSource = memStream;
                bmi.EndInit();
                ImageCropPreview.Source = bmi;
            }
            catch { }
        }

        private void SortQueue()
        {
            try
            {
                // Sort Queue
                List<Queue.QueueElement> queueElements = ListBoxQueue.Items.OfType<Queue.QueueElement>().ToList();

                switch (settingsDB.SortQueueBy)
                {
                    case 0:
                        queueElements = queueElements.OrderBy(queueElements => queueElements.DateAdded).ToList();
                        break;
                    case 1:
                        queueElements = queueElements.OrderByDescending(queueElements => queueElements.DateAdded).ToList();
                        break;
                    case 2:
                        queueElements = queueElements.OrderBy(queueElements => queueElements.VideoDB.MIFrameCount).ToList();
                        break;
                    case 3:
                        queueElements = queueElements.OrderByDescending(queueElements => queueElements.VideoDB.MIFrameCount).ToList();
                        break;
                    case 4:
                        queueElements = queueElements.OrderBy(queueElements => queueElements.VideoDB.OutputFileName).ToList();
                        break;
                    case 5:
                        queueElements = queueElements.OrderByDescending(queueElements => queueElements.VideoDB.OutputFileName).ToList();
                        break;
                    default:
                        queueElements = queueElements.OrderByDescending(queueElements => queueElements.DateAdded).ToList();
                        break;
                }

                ListBoxQueue.Items.Clear();
                foreach (var queueElement in queueElements)
                {
                    ListBoxQueue.Items.Add(queueElement);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void ComboBoxChunkingMethod_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (startupLock) return;
            settingsDB.ChunkingMethod = ComboBoxChunkingMethod.SelectedIndex;
            settingsDB.ReencodeMethod = ComboBoxReencodeMethod.SelectedIndex;
            try
            {
                Directory.CreateDirectory(Path.Combine(Global.AppData, "NEAV1E"));
                File.WriteAllText(Path.Combine(Global.AppData, "NEAV1E", "settings.json"), JsonConvert.SerializeObject(settingsDB, Formatting.Indented));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void TextBoxChunkLength_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (startupLock) return;
            settingsDB.ChunkLength = TextBoxChunkLength.Text;
            settingsDB.PySceneDetectThreshold = TextBoxPySceneDetectThreshold.Text;
            try
            {
                Directory.CreateDirectory(Path.Combine(Global.AppData, "NEAV1E"));
                File.WriteAllText(Path.Combine(Global.AppData, "NEAV1E", "settings.json"), JsonConvert.SerializeObject(settingsDB, Formatting.Indented));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void ComboBoxWorkerCount_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (startupLock) return;
            if (settingsDB.OverrideWorkerCount) return;
            settingsDB.WorkerCount = ComboBoxWorkerCount.SelectedIndex;
            try
            {
                Directory.CreateDirectory(Path.Combine(Global.AppData, "NEAV1E"));
                File.WriteAllText(Path.Combine(Global.AppData, "NEAV1E", "settings.json"), JsonConvert.SerializeObject(settingsDB, Formatting.Indented));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void TextBoxWorkerCount_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (startupLock) return;
            if (!settingsDB.OverrideWorkerCount) return;
            settingsDB.WorkerCount = int.Parse(TextBoxWorkerCount.Text);
            try
            {
                Directory.CreateDirectory(Path.Combine(Global.AppData, "NEAV1E"));
                File.WriteAllText(Path.Combine(Global.AppData, "NEAV1E", "settings.json"), JsonConvert.SerializeObject(settingsDB, Formatting.Indented));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void ToggleSwitchQueueParallel_Toggled(object sender, RoutedEventArgs e)
        {
            if (startupLock) return;
            settingsDB.QueueParallel = ToggleSwitchQueueParallel.IsOn;
            try
            {
                Directory.CreateDirectory(Path.Combine(Global.AppData, "NEAV1E"));
                File.WriteAllText(Path.Combine(Global.AppData, "NEAV1E", "settings.json"), JsonConvert.SerializeObject(settingsDB, Formatting.Indented));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void DeleteQueueItems()
        {
            if (ListBoxQueue.SelectedItem == null) return;
            if (ProgramState != 0) return;
            if (ListBoxQueue.SelectedItems.Count > 1)
            {
                List<Queue.QueueElement> items = ListBoxQueue.SelectedItems.OfType<Queue.QueueElement>().ToList();
                foreach (var item in items)
                {
                    ListBoxQueue.Items.Remove(item);
                    try
                    {
                        File.Delete(Path.Combine(Global.AppData, "NEAV1E", "Queue", item.VideoDB.InputFileName + "_" + item.UniqueIdentifier + ".json"));
                    }
                    catch { }
                }
            }
            else
            {
                Queue.QueueElement tmp = (Queue.QueueElement)ListBoxQueue.SelectedItem;
                ListBoxQueue.Items.Remove(ListBoxQueue.SelectedItem);
                try
                {
                    File.Delete(Path.Combine(Global.AppData, "NEAV1E", "Queue", tmp.VideoDB.InputFileName + "_" + tmp.UniqueIdentifier + ".json"));
                }
                catch { }
            }
        }

        private void LoadSettings()
        {
            if (settingsDB.OverrideWorkerCount)
            {
                ComboBoxWorkerCount.Visibility = Visibility.Hidden;
                TextBoxWorkerCount.Visibility = Visibility.Visible;
                if (settingsDB.WorkerCount != 99999999)
                    TextBoxWorkerCount.Text = settingsDB.WorkerCount.ToString();
            }
            else
            {
                ComboBoxWorkerCount.Visibility = Visibility.Visible;
                TextBoxWorkerCount.Visibility = Visibility.Hidden;
                if (settingsDB.WorkerCount != 99999999)
                    ComboBoxWorkerCount.SelectedIndex = settingsDB.WorkerCount;
            }

            ComboBoxChunkingMethod.SelectedIndex = settingsDB.ChunkingMethod;
            ComboBoxReencodeMethod.SelectedIndex = settingsDB.ReencodeMethod;
            TextBoxChunkLength.Text = settingsDB.ChunkLength;
            TextBoxPySceneDetectThreshold.Text = settingsDB.PySceneDetectThreshold;
            ToggleSwitchQueueParallel.IsOn = settingsDB.QueueParallel;

            // Sets Temp Path
            Global.Temp = settingsDB.TempPath;
            Logging = settingsDB.Logging;

            // Set Theme
            try
            {
                ThemeManager.Current.ChangeTheme(this, settingsDB.Theme);
            }
            catch { }
            try
            {
                if (settingsDB.BGImage != null)
                {
                    Uri fileUri = new(settingsDB.BGImage);
                    bgImage.Source = new BitmapImage(fileUri);

                    SolidColorBrush bg = new(Color.FromArgb(150, 100, 100, 100));
                    SolidColorBrush fg = new(Color.FromArgb(180, 100, 100, 100));
                    if (settingsDB.BaseTheme == 1)
                    {
                        // Dark
                        bg = new(Color.FromArgb(150, 20, 20, 20));
                        fg = new(Color.FromArgb(180, 20, 20, 20));
                    }

                    TabControl.Background = bg;
                    ListBoxAudioTracks.Background = fg;
                    ListBoxSubtitleTracks.Background = fg;
                }
                else
                {
                    bgImage.Source = null;
                }
            }
            catch { }
        }

        private void AddToQueue(string identifier, bool skipSubs)
        {
            lockQueue = true;
            if (string.IsNullOrEmpty(videoDB.InputPath))
            {
                // Throw Error
                MessageBox.Show(LocalizedStrings.Instance["MessageNoInput"], LocalizedStrings.Instance["Error"], MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (string.IsNullOrEmpty(videoDB.OutputPath))
            {
                // Throw Error
                MessageBox.Show(LocalizedStrings.Instance["MessageNoOutput"], LocalizedStrings.Instance["Error"], MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (videoDB.InputPath == videoDB.OutputPath)
            {
                // Throw Error
                MessageBox.Show(LocalizedStrings.Instance["MessageSameInputOutput"], LocalizedStrings.Instance["Error"], MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Queue.QueueElement queueElement = new();
            Audio.CommandGenerator audioCommandGenerator = new();
            Subtitle.CommandGenerator subCommandGenerator = new();

            queueElement.UniqueIdentifier = identifier;
            queueElement.Input = videoDB.InputPath;
            queueElement.Output = videoDB.OutputPath;
            queueElement.VideoCommand = CheckBoxCustomVideoSettings.IsOn ? TextBoxCustomVideoSettings.Text : GenerateEncoderCommand();
            queueElement.VideoHDRMuxCommand = GenerateMKVMergeHDRCommand();
            queueElement.AudioCommand = audioCommandGenerator.Generate(ListBoxAudioTracks.Items);
            queueElement.SubtitleCommand = skipSubs ? null : subCommandGenerator.GenerateSoftsub(ListBoxSubtitleTracks.Items);
            queueElement.SubtitleBurnCommand = subCommandGenerator.GenerateHardsub(ListBoxSubtitleTracks.Items, identifier);
            queueElement.FilterCommand = GenerateVideoFilters();
            queueElement.FrameCount = videoDB.MIFrameCount;
            queueElement.EncodingMethod = ComboBoxVideoEncoder.SelectedIndex;
            queueElement.ChunkingMethod = ComboBoxChunkingMethod.SelectedIndex;
            queueElement.ReencodeMethod = ComboBoxReencodeMethod.SelectedIndex;
            queueElement.Passes = CheckBoxTwoPassEncoding.IsOn ? 2 : 1;
            queueElement.ChunkLength = int.Parse(TextBoxChunkLength.Text);
            queueElement.PySceneDetectThreshold = float.Parse(TextBoxPySceneDetectThreshold.Text);
            queueElement.VFR = CheckBoxVideoVFR.IsChecked == true;
            queueElement.Preset = PresetSettings;
            queueElement.VideoDB = videoDB;

            if (ToggleSwitchFilterDeinterlace.IsOn && ComboBoxFiltersDeinterlace.SelectedIndex is 1 or 2)
            {
                queueElement.FrameCount += queueElement.FrameCount;
            }

            // Add to Queue
            ListBoxQueue.Items.Add(queueElement);

            Directory.CreateDirectory(Path.Combine(Global.AppData, "NEAV1E", "Queue"));

            // Save as JSON
            File.WriteAllText(Path.Combine(Global.AppData, "NEAV1E", "Queue", videoDB.InputFileName + "_" + identifier + ".json"), JsonConvert.SerializeObject(queueElement, Formatting.Indented));

            lockQueue = false;

            SortQueue();
        }

        private void AutoPauseResume()
        {
            TimeSpan idleTime = win32.IdleDetection.GetInputIdleTime();
            double time = idleTime.TotalSeconds;
            
            Debug.WriteLine("AutoPauseResume() => " + time.ToString() + " Seconds");
            if (ProgramState is 1)
            {
                // Pause
                if (time < 40.0)
                {
                    Dispatcher.Invoke(() => ImageStartStop.Source = new BitmapImage(new Uri(@"/NotEnoughAV1Encodes;component/resources/img/resume.png", UriKind.Relative)));
                    Dispatcher.Invoke(() => LabelStartPauseButton.Content = LocalizedStrings.Instance["Resume"]);
                    Dispatcher.Invoke(() => Title = "NEAV1E - " + LocalizedStrings.Instance["ToggleSwitchAutoPauseResume"] + " => Paused");

                    // Pause all PIDs
                    foreach (int pid in Global.LaunchedPIDs)
                    {
                        Suspend.SuspendProcessTree(pid);
                    }

                    ProgramState = 2;
                }
            }
            else if (ProgramState is 2)
            {
                Dispatcher.Invoke(() => Title = "NEAV1E - " + LocalizedStrings.Instance["ToggleSwitchAutoPauseResume"] + " => Paused - System IDLE since " + time.ToString() + " seconds");
                // Resume
                if (time > 60.0)
                {
                    Dispatcher.Invoke(() => ImageStartStop.Source = new BitmapImage(new Uri(@"/NotEnoughAV1Encodes;component/resources/img/pause.png", UriKind.Relative)));
                    Dispatcher.Invoke(() => LabelStartPauseButton.Content = LocalizedStrings.Instance["Pause"]);
                    Dispatcher.Invoke(() => Title = "NEAV1E - " + LocalizedStrings.Instance["ToggleSwitchAutoPauseResume"] + " => Encoding");

                    // Resume all PIDs
                    if (ProgramState is 2)
                    {
                        foreach (int pid in Global.LaunchedPIDs)
                        {
                            Resume.ResumeProcessTree(pid);
                        }
                    }

                    ProgramState = 1;
                }
            }
        }

        private void Shutdown()
        {
            if (settingsDB.ShutdownAfterEncode)
            {
                Process.Start("shutdown.exe", "/s /t 0");
            }
        }

        private void DeleteTempFiles(Queue.QueueElement queueElement, DateTime startTime)
        {
            string errorText = "";
            if (queueElement.Error)
            {
                errorText = " - " + queueElement.ErrorCount.ToString() + " " + LocalizedStrings.Instance["ErrorsDetected"];
            }

            if (!File.Exists(queueElement.VideoDB.OutputPath)) {
                queueElement.Status = LocalizedStrings.Instance["OutputErrorDetected"] + errorText;
                return;
            }

            FileInfo videoOutput = new(queueElement.VideoDB.OutputPath);
            if (videoOutput.Length <= 50000) {
                queueElement.Status = LocalizedStrings.Instance["MuxingErrorDetected"] + errorText;
                return;
            }

            TimeSpan timespent = DateTime.Now - startTime;
            try {
                queueElement.Status = LocalizedStrings.Instance["FinishedEncoding"] + " " + timespent.ToString("hh\\:mm\\:ss") + " - avg " + Math.Round(queueElement.FrameCount / timespent.TotalSeconds, 2) + "fps" + errorText;
            }
            catch
            {
                queueElement.Status = LocalizedStrings.Instance["FinishedEncoding"] + " " + timespent.ToString("hh\\:mm\\:ss") + " - Error calculating average FPS" + errorText;
            }


            if (settingsDB.DeleteTempFiles && queueElement.Error == false) {
                try {
                    DirectoryInfo tmp = new(Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier));
                    tmp.Delete(true);
                } catch {
                    queueElement.Status = LocalizedStrings.Instance["DeleteErrorDetected"] + errorText;
                }
            }
        }
        #endregion

        #region Video Filters
        private string GenerateVideoFilters()
        {
            bool crop = ToggleSwitchFilterCrop.IsOn;
            bool rotate = ToggleSwitchFilterRotate.IsOn;
            bool resize = ToggleSwitchFilterResize.IsOn;
            bool deinterlace = ToggleSwitchFilterDeinterlace.IsOn;
            bool fps = ComboBoxVideoFrameRate.SelectedIndex != 0;
            bool _oneFilter = false;

            string FilterCommand = "";

            if (crop || rotate || resize || deinterlace || fps)
            {
                FilterCommand = " -vf ";
                if (resize)
                {
                    // Has to be last, due to scaling algorithm
                    FilterCommand += VideoFiltersResize();
                    _oneFilter = true;
                }
                if (crop)
                {
                    if (_oneFilter) { FilterCommand += ","; }
                    FilterCommand += VideoFiltersCrop();
                    _oneFilter = true;
                }
                if (rotate)
                {
                    if (_oneFilter) { FilterCommand += ","; }
                    FilterCommand += VideoFiltersRotate();
                    _oneFilter = true;
                }
                if (deinterlace)
                {
                    if (_oneFilter) { FilterCommand += ","; }
                    FilterCommand += VideoFiltersDeinterlace();
                    _oneFilter = true;
                }
                if (fps)
                {
                    if (_oneFilter) { FilterCommand += ","; }
                    FilterCommand += GenerateFFmpegFramerate();
                }
            }


            return FilterCommand;
        }

        private string GenerateFFmpegFramerate()
        {
            string settings = "";

            settings = "fps=" + ComboBoxVideoFrameRate.Text;
            if (ComboBoxVideoFrameRate.SelectedIndex == 6) { settings = "fps=24000/1001"; }
            if (ComboBoxVideoFrameRate.SelectedIndex == 9) { settings = "fps=30000/1001"; }
            if (ComboBoxVideoFrameRate.SelectedIndex == 13) { settings = "fps=60000/1001"; }

            return settings;
        }

        private string VideoFiltersCrop()
        {
            // Sets the values for cropping the video
            string widthNew = "";
            string heightNew = "";
            try
            {
                widthNew = (int.Parse(TextBoxFiltersCropRight.Text) + int.Parse(TextBoxFiltersCropLeft.Text)).ToString();
                heightNew = (int.Parse(TextBoxFiltersCropTop.Text) + int.Parse(TextBoxFiltersCropBottom.Text)).ToString();
            }
            catch
            {
                widthNew = "0";
                heightNew = "0";
            }

            return "crop=iw-" + widthNew + ":ih-" + heightNew + ":" + TextBoxFiltersCropLeft.Text + ":" + TextBoxFiltersCropTop.Text;
        }

        private string VideoFiltersRotate()
        {
            // Sets the values for rotating the video
            if (ComboBoxFiltersRotate.SelectedIndex == 1) return "transpose=1";
            else if (ComboBoxFiltersRotate.SelectedIndex == 2) return "transpose=2,transpose=2";
            else if (ComboBoxFiltersRotate.SelectedIndex == 3) return "transpose=2";
            else return ""; // If user selected no ratation but still has it enabled
        }

        private string VideoFiltersDeinterlace()
        {
            int filterIndex = ComboBoxFiltersDeinterlace.SelectedIndex;
            string filter = "";

            if (filterIndex == 0)
            {
                filter = "bwdif=mode=0";
            }
            else if (filterIndex == 1)
            {
                filter = "estdif=mode=0";
            }
            else if (filterIndex == 2)
            {
                string bin = Path.Combine(Directory.GetCurrentDirectory(), "Apps", "nnedi", "nnedi3_weights.bin");
                bin = bin.Replace("\u005c", "\u005c\u005c").Replace(":", "\u005c:");
                filter = "nnedi=weights='" + bin + "'";
            }
            else if (filterIndex == 3)
            {
                filter = "yadif=mode=0";
            }

            return filter;
        }

        private string VideoFiltersResize()
        {
            // Auto Set Width
            if (TextBoxFiltersResizeWidth.Text == "0")
            {
                return "scale=trunc(oh*a/2)*2:" + TextBoxFiltersResizeHeight.Text + ":flags=" + ComboBoxResizeAlgorithm.Text;
            }

            // Auto Set Height
            if (TextBoxFiltersResizeHeight.Text == "0")
            {
                return "scale=" + TextBoxFiltersResizeWidth.Text + ":trunc(ow/a/2)*2:flags=" + ComboBoxResizeAlgorithm.Text;
            }

            return "scale=" + TextBoxFiltersResizeWidth.Text + ":" + TextBoxFiltersResizeHeight.Text + ":flags=" + ComboBoxResizeAlgorithm.Text;

        }
        #endregion

        #region Encoder Settings
        private string GenerateEncoderCommand()
        {
            string settings = GenerateFFmpegColorSpace() + " ";

            string encoderSetting = ComboBoxVideoEncoder.SelectedIndex switch
            {
                0 => GenerateAomFFmpegCommand(),
                1 => GenerateRav1eFFmpegCommand(),
                2 => GenerateSvtAV1FFmpegCommand(),
                3 => GenerateVpxVP9Command(),
                5 => GenerateAomencCommand(),
                6 => GenerateRav1eCommand(),
                7 => GenerateSvtAV1Command(),
                9 => GenerateHEVCFFmpegCommand(),
                10 => GenerateAVCFFmpegCommand(),
                12 => GenerateQuickSyncCommand(),
                13 => GenerateNVENCCommand(),
                _ => ""
            };

            return settings + encoderSetting;
        }

        private string GenerateAomFFmpegCommand()
        {
            string settings = "-c:v libaom-av1";

            // Quality / Bitrate Selection
            string quality = ComboBoxQualityMode.SelectedIndex switch
            {
                0 => " -crf " + SliderQualityAOMFFMPEG.Value + " -b:v 0",
                1 => " -crf " + SliderQualityAOMFFMPEG.Value + " -b:v " + TextBoxMaxBitrateAOMFFMPEG.Text + "k",
                2 => " -b:v " + TextBoxMinBitrateAOMFFMPEG.Text + "k",
                3 => " -minrate " + TextBoxMinBitrateAOMFFMPEG.Text + "k -b:v " + TextBoxAVGBitrateAOMFFMPEG.Text + "k -maxrate " + TextBoxMaxBitrateAOMFFMPEG.Text + "k",
                4 => " -crf {q_vmaf} -b:v 0",
                _ => ""
            };

            // Preset
            settings += quality + " -cpu-used " + SliderEncoderPreset.Value;

            // Advanced Settings
            if (ToggleSwitchAdvancedSettings.IsOn == false)
            {
                settings += " -threads 4 -tile-columns 2 -tile-rows 1 -g " + GenerateKeyFrameInerval();
            }
            else
            {
                settings += " -threads " + ComboBoxAomencThreads.Text +                                      // Threads
                            " -tile-columns " + ComboBoxAomencTileColumns.Text +                             // Tile Columns
                            " -tile-rows " + ComboBoxAomencTileRows.Text +                                   // Tile Rows
                            " -lag-in-frames " + TextBoxAomencLagInFrames.Text +                             // Lag in Frames
                            " -aq-mode " + ComboBoxAomencAQMode.SelectedIndex +                              // AQ-Mode
                            " -tune " + ComboBoxAomencTune.Text;                                             // Tune

                if (TextBoxAomencMaxGOP.Text != "0") 
                    settings += " -g " + TextBoxAomencMaxGOP.Text;                                           // Keyframe Interval
                if (CheckBoxAomencRowMT.IsChecked == false) 
                    settings += " -row-mt 0";                                                                // Row Based Multithreading
                if (CheckBoxAomencCDEF.IsChecked == false) 
                    settings += " -enable-cdef 0";                                                           // Constrained Directional Enhancement Filter
                if (CheckBoxRealTimeMode.IsOn) 
                    settings += " -usage realtime ";                                                         // Real Time Mode

                if (CheckBoxAomencARNRMax.IsChecked == true)
                {
                    settings += " -arnr-max-frames " + ComboBoxAomencARNRMax.Text;                           // ARNR Maxframes
                    settings += " -arnr-strength " + ComboBoxAomencARNRStrength.Text;                        // ARNR Strength
                }

                settings += " -aom-params " +
                            "tune-content=" + ComboBoxAomencTuneContent.Text +                               // Tune-Content
                            ":sharpness=" + ComboBoxAomencSharpness.Text +                                   // Sharpness (Filter)
                            ":enable-keyframe-filtering=" + ComboBoxAomencKeyFiltering.SelectedIndex;        // Key Frame Filtering

                if (ComboBoxAomencColorPrimaries.SelectedIndex != 0)
                    settings += ":color-primaries=" + ComboBoxAomencColorPrimaries.Text;                     // Color Primaries
                if (ComboBoxAomencColorTransfer.SelectedIndex != 0)
                    settings += ":transfer-characteristics=" + ComboBoxAomencColorTransfer.Text;             // Color Transfer
                if (ComboBoxAomencColorMatrix.SelectedIndex != 0)
                    settings += ":matrix-coefficients=" + ComboBoxAomencColorMatrix.Text;                    // Color Matrix
            }

            return settings;
        }

        private string GenerateRav1eFFmpegCommand()
        {
            string settings = "-c:v librav1e";

            // Quality / Bitrate Selection
            string quality = ComboBoxQualityModeRAV1EFFMPEG.SelectedIndex switch
            {
                0 => " -qp " + SliderQualityRAV1EFFMPEG.Value,
                1 => " -b:v " + TextBoxBitrateRAV1EFFMPEG.Text + "k",
                _ => ""
            };

            // Preset
            settings += quality + " -speed " + SliderEncoderPreset.Value;

            // Advanced Settings
            if (ToggleSwitchAdvancedSettings.IsOn == false)
            {
                settings += " -tile-columns 2 -tile-rows 1 -g " + GenerateKeyFrameInerval() + " -rav1e-params threads=4";
            }
            else
            {
                settings += " -tile-columns " + ComboBoxRav1eTileColumns.SelectedIndex +                     // Tile Columns
                            " -tile-rows " + ComboBoxRav1eTileRows.SelectedIndex;                            // Tile Rows

                settings += " -rav1e-params " +
                            "threads=" + ComboBoxRav1eThreads.SelectedIndex +                                // Threads
                            ":rdo-lookahead-frames=" + TextBoxRav1eLookahead.Text +                          // RDO Lookahead
                            ":tune=" + ComboBoxRav1eTune.Text;                                               // Tune

                if (TextBoxRav1eMaxGOP.Text != "0") 
                    settings += ":keyint=" + TextBoxRav1eMaxGOP.Text;                                        // Keyframe Interval

                if (ComboBoxRav1eColorPrimaries.SelectedIndex != 0) 
                    settings += ":primaries=" + ComboBoxRav1eColorPrimaries.Text;                            // Color Primaries
                if (ComboBoxRav1eColorTransfer.SelectedIndex != 0)
                    settings += ":transfer=" + ComboBoxRav1eColorTransfer.Text;                              // Color Transfer
                if (ComboBoxRav1eColorMatrix.SelectedIndex != 0)
                    settings += ":matrix=" + ComboBoxRav1eColorMatrix.Text;                                  // Color Matrix
            }

            return settings;
        }

        private string GenerateSvtAV1FFmpegCommand()
        {
            string settings = "-c:v libsvtav1";

            // Quality / Bitrate Selection
            string quality = ComboBoxQualityModeSVTAV1FFMPEG.SelectedIndex switch
            {
                0 => " -rc 0 -qp " + SliderQualitySVTAV1FFMPEG.Value,
                1 => " -rc 1 -b:v " + TextBoxBitrateSVTAV1FFMPEG.Text + "k",
                _ => ""
            };

            // Preset
            settings += quality + " -preset " + SliderEncoderPreset.Value;

            // Advanced Settings
            if (ToggleSwitchAdvancedSettings.IsOn == false)
            {
                settings += " -g " + GenerateKeyFrameInerval();
            }
            else
            {
                settings += " -tile_columns " + ComboBoxSVTAV1TileColumns.Text +                             // Tile Columns
                            " -tile_rows " + ComboBoxSVTAV1TileRows.Text +                                   // Tile Rows
                            " -g " + TextBoxSVTAV1MaxGOP.Text +                                              // Keyframe Interval
                            " -la_depth " + TextBoxSVTAV1Lookahead.Text +                                    // Lookahead
                            " -svtav1-params " +
                            "aq-mode=" + ComboBoxSVTAV1AQMode.Text +                                         // AQ Mode
                            ":film-grain=" + TextBoxSVTAV1FilmGrain.Text +                                   // Film Grain
                            ":film-grain-denoise=" + TextBoxSVTAV1FilmGrainDenoise.Text;                     // Film Grain Denoise
            }

            return settings;
        }

        private string GenerateVpxVP9Command()
        {
            string settings = "-c:v libvpx-vp9";

            // Quality / Bitrate Selection
            string quality = ComboBoxQualityModeVP9FFMPEG.SelectedIndex switch
            {
                0 => " -crf " + SliderQualityVP9FFMPEG.Value + " -b:v 0",
                1 => " -crf " + SliderQualityVP9FFMPEG.Value + " -b:v " + TextBoxMaxBitrateVP9FFMPEG.Text + "k",
                2 => " -b:v " + TextBoxAVGBitrateVP9FFMPEG.Text + "k",
                3 => " -minrate " + TextBoxMinBitrateVP9FFMPEG.Text + "k -b:v " + TextBoxAVGBitrateVP9FFMPEG.Text + "k -maxrate " + TextBoxMaxBitrateVP9FFMPEG.Text + "k",
                _ => ""
            };

            // Preset
            settings += quality + " -cpu-used " + SliderEncoderPreset.Value;

            // Advanced Settings
            if (ToggleSwitchAdvancedSettings.IsOn == false)
            {
                settings += " -threads 4 -tile-columns 2 -tile-rows 1 -g " + GenerateKeyFrameInerval();
            }
            else
            {
                settings += " -threads " + ComboBoxVP9Threads.Text +                                         // Max Threads
                            " -tile-columns " + ComboBoxVP9TileColumns.SelectedIndex +                       // Tile Columns
                            " -tile-rows " + ComboBoxVP9TileRows.SelectedIndex +                             // Tile Rows
                            " -lag-in-frames " + TextBoxVP9LagInFrames.Text +                                // Lag in Frames
                            " -g " + TextBoxVP9MaxKF.Text +                                                  // Max GOP
                            " -aq-mode " + ComboBoxVP9AQMode.SelectedIndex +                                 // AQ-Mode
                            " -tune " + ComboBoxVP9ATune.SelectedIndex +                                     // Tune
                            " -tune-content " + ComboBoxVP9ATuneContent.SelectedIndex;                       // Tune-Content

                if (CheckBoxVP9ARNR.IsChecked == true)
                {
                    settings += " -arnr-maxframes " + ComboBoxAomencVP9Max.Text +                            // ARNR Max Frames
                                " -arnr-strength " + ComboBoxAomencVP9Strength.Text +                        // ARNR Strength
                                " -arnr-type " + ComboBoxAomencVP9ARNRType.Text;                             // ARNR Type
                }
            }

            return settings;
        }

        private string GenerateAomencCommand()
        {
            string settings = "-f yuv4mpegpipe - | " +
                              "\"" + Path.Combine(Directory.GetCurrentDirectory(), "Apps", "aomenc", "aomenc.exe") + "\" -";

            // Quality / Bitrate Selection
            string quality = ComboBoxQualityModeAOMENC.SelectedIndex switch
            {
                0 => " --cq-level=" + SliderQualityAOMENC.Value + " --end-usage=q",
                1 => " --cq-level=" + SliderQualityAOMENC.Value + " --target-bitrate=" + TextBoxBitrateAOMENC.Text + " --end-usage=cq",
                2 => " --target-bitrate=" + TextBoxBitrateAOMENC.Text + " --end-usage=vbr",
                3 => " --target-bitrate=" + TextBoxBitrateAOMENC.Text + " --end-usage=cbr",
                _ => ""
            };

            // Preset
            settings += quality + " --cpu-used=" + SliderEncoderPreset.Value;

            // Advanced Settings
            if (ToggleSwitchAdvancedSettings.IsOn == false)
            {
                settings += " --threads=4 --tile-columns=2 --tile-rows=1 --kf-max-dist=" + GenerateKeyFrameInerval();
            }
            else
            {
                settings += " --threads=" + ComboBoxAomencThreads.Text +                                     // Threads
                            " --tile-columns=" + ComboBoxAomencTileColumns.Text +                            // Tile Columns
                            " --tile-rows=" + ComboBoxAomencTileRows.Text +                                  // Tile Rows
                            " --lag-in-frames=" + TextBoxAomencLagInFrames.Text +                            // Lag in Frames
                            " --sharpness=" + ComboBoxAomencSharpness.Text +                                 // Sharpness (Filter)
                            " --aq-mode=" + ComboBoxAomencAQMode.SelectedIndex +                             // AQ-Mode
                            " --enable-keyframe-filtering=" + ComboBoxAomencKeyFiltering.SelectedIndex +     // Key Frame Filtering
                            " --tune=" + ComboBoxAomencTune.Text +                                           // Tune
                            " --tune-content=" + ComboBoxAomencTuneContent.Text;                             // Tune-Content

                if (TextBoxAomencMaxGOP.Text != "0")
                    settings += " --kf-max-dist=" + TextBoxAomencMaxGOP.Text;                                // Keyframe Interval
                if (CheckBoxAomencRowMT.IsChecked == false)
                    settings += " --row-mt=0";                                                               // Row Based Multithreading

                if (ComboBoxAomencColorPrimaries.SelectedIndex != 0)
                    settings += " --color-primaries=" + ComboBoxAomencColorPrimaries.Text;                   // Color Primaries
                if (ComboBoxAomencColorTransfer.SelectedIndex != 0)
                    settings += " --transfer-characteristics=" + ComboBoxAomencColorTransfer.Text;           // Color Transfer
                if (ComboBoxAomencColorMatrix.SelectedIndex != 0)
                    settings += " --matrix-coefficients=" + ComboBoxAomencColorMatrix.Text;                  // Color Matrix

                if (CheckBoxAomencCDEF.IsChecked == false)
                    settings += " --enable-cdef=0";                                                          // Constrained Directional Enhancement Filter

                if (CheckBoxAomencARNRMax.IsChecked == true)
                {
                    settings += " --arnr-maxframes=" + ComboBoxAomencARNRMax.Text;                           // ARNR Maxframes
                    settings += " --arnr-strength=" + ComboBoxAomencARNRStrength.Text;                       // ARNR Strength
                }

                if (CheckBoxRealTimeMode.IsOn)
                    settings += " --rt";                                                                     // Real Time Mode
            }

            return settings;
        }

        private string GenerateRav1eCommand()
        {
            string settings = "-f yuv4mpegpipe - | " +
                               "\"" + Path.Combine(Directory.GetCurrentDirectory(), "Apps", "rav1e", "rav1e.exe") + "\" - -y";

            // Quality / Bitrate Selection
            string quality = ComboBoxQualityModeRAV1E.SelectedIndex switch
            {
                0 => " --quantizer " + SliderQualityRAV1E.Value,
                1 => " --bitrate " + TextBoxBitrateRAV1E.Text,
                _ => ""
            };

            // Preset
            settings += quality + " --speed " + SliderEncoderPreset.Value;

            // Advanced Settings
            if (ToggleSwitchAdvancedSettings.IsOn == false)
            {
                settings += " --threads 4 --tile-cols 2 --tile-rows 1 --keyint " + GenerateKeyFrameInerval();
            }
            else
            {
                settings += " --threads " + ComboBoxRav1eThreads.SelectedIndex +                             // Threads
                            " --tile-cols " + ComboBoxRav1eTileColumns.SelectedIndex +                       // Tile Columns
                            " --tile-rows " + ComboBoxRav1eTileRows.SelectedIndex +                          // Tile Rows
                            " --rdo-lookahead-frames " + TextBoxRav1eLookahead.Text +                        // RDO Lookahead
                            " --tune " + ComboBoxRav1eTune.Text;                                             // Tune

                if (TextBoxRav1eMaxGOP.Text != "0")
                    settings += " --keyint " + TextBoxRav1eMaxGOP.Text;                                      // Keyframe Interval

                if (ComboBoxRav1eColorPrimaries.SelectedIndex != 0)
                    settings += " --primaries " + ComboBoxRav1eColorPrimaries.Text;                          // Color Primaries
                if (ComboBoxRav1eColorTransfer.SelectedIndex != 0)
                    settings += " --transfer " + ComboBoxRav1eColorTransfer.Text;                            // Color Transfer
                if (ComboBoxRav1eColorMatrix.SelectedIndex != 0)
                    settings += " --matrix " + ComboBoxRav1eColorMatrix.Text;                                // Color Matrix
            }

            return settings;
        }

        private string GenerateSvtAV1Command()
        {
            string settings = "-nostdin -f yuv4mpegpipe - | " +
                              "\"" + Path.Combine(Directory.GetCurrentDirectory(), "Apps", "svt-av1", "SvtAv1EncApp.exe") + "\" -i stdin";

            // Quality / Bitrate Selection
            string quality = ComboBoxQualityModeSVTAV1.SelectedIndex switch
            {
                0 => " --rc 0 --crf " + SliderQualitySVTAV1.Value,
                1 => " --rc 1 --tbr " + TextBoxBitrateSVTAV1.Text,
                _ => ""
            };

            // Preset
            settings += quality +" --preset " + SliderEncoderPreset.Value;

            // Advanced Settings
            if (ToggleSwitchAdvancedSettings.IsOn == false)
            {
                settings += " --keyint " + GenerateKeyFrameInerval();

            }
            else
            {
                settings += " --tile-columns " + ComboBoxSVTAV1TileColumns.Text +                             // Tile Columns
                            " --tile-rows " + ComboBoxSVTAV1TileRows.Text +                                   // Tile Rows
                            " --keyint " + TextBoxSVTAV1MaxGOP.Text +                                         // Keyframe Interval
                            " --lookahead " + TextBoxSVTAV1Lookahead.Text +                                   // Lookahead
                            " --aq-mode " + ComboBoxSVTAV1AQMode.Text +                                       // AQ Mode
                            " --film-grain " + TextBoxSVTAV1FilmGrain.Text +                                  // Film Grain
                            " --film-grain-denoise " +  TextBoxSVTAV1FilmGrainDenoise.Text;                   // Film Grain Denoise                      
            }

            return settings;
        }

        private string GenerateHEVCFFmpegCommand()
        {
            string settings = "-c:v libx265";

            // Quality / Bitrate Selection
            string quality = ComboBoxQualityModeX26x.SelectedIndex switch
            {
                0 => " -crf " + SliderQualityX26x.Value,
                1 => " -b:v " + TextBoxBitrateX26x.Text + "k",
                _ => ""
            };

            // Preset
            settings += quality + " -preset " + GenerateMPEGEncoderSpeed();

            return settings;
        }

        private string GenerateAVCFFmpegCommand()
        {
            string settings = "-c:v libx264";

            // Quality / Bitrate Selection
            string quality = ComboBoxQualityModeX26x.SelectedIndex switch
            {
                0 => " -crf " + SliderQualityX26x.Value,
                1 => " -b:v " + TextBoxBitrateX26x.Text + "k",
                _ => ""
            };

            // Preset
            settings += quality + " -preset " + GenerateMPEGEncoderSpeed();

            return settings;
        }

        private string GenerateQuickSyncCommand()
        {
            string settings = "-f yuv4mpegpipe - | " +
                    "\"" + Path.Combine(Directory.GetCurrentDirectory(), "Apps", "qsvenc", "QSVEncC64.exe") + "\" --y4m -i -";

            // Codec
            settings += " --codec av1";

            // Quality / Bitrate Selection
            string quality = ComboBoxQualityModeQSVAV1.SelectedIndex switch
            {
                0 => " --cqp " + SliderQualityQSVAV1.Value,
                1 => " --icq " + SliderQualityQSVAV1.Value,
                2 => " --vbr " + TextBoxBitrateQSVAV1.Text,
                3 => " --cbr " + TextBoxBitrateQSVAV1.Text,
                _ => ""
            };

            // Preset
            settings += quality + " --quality " + GenerateQuickSyncEncoderSpeed();

            // Bit-Depth
            settings += " --output-depth ";
            settings += ComboBoxVideoBitDepthLimited.SelectedIndex switch
            {
                0 => "8",
                1 => "10",
                _ => "8"
            };

            // Output Colorspace
            settings += " --output-csp ";
            settings += ComboBoxColorFormat.SelectedIndex switch
            {
                0 => "i420",
                1 => "i422",
                2 => "i444",
                _ => "i420"
            };


            return settings;
        }

        private string GenerateNVENCCommand()
        {
            string settings = "-f yuv4mpegpipe - | " +
                    "\"" + Path.Combine(Directory.GetCurrentDirectory(), "Apps", "nvenc", "NVEncC64.exe") + "\" --y4m -i -";

            // Codec
            settings += " --codec av1";

            // Quality / Bitrate Selection
            string quality = ComboBoxQualityModeQSVAV1.SelectedIndex switch
            {
                0 => " --cqp " + SliderQualityQSVAV1.Value,
                1 => " --vbr " + TextBoxBitrateQSVAV1.Text,
                2 => " --cbr " + TextBoxBitrateQSVAV1.Text,
                _ => ""
            };

            // Preset
            settings += quality + " --preset " + GenerateNVENCEncoderSpeed();

            // Bit-Depth
            settings += " --output-depth ";
            settings += ComboBoxVideoBitDepthLimited.SelectedIndex switch
            {
                0 => "8",
                1 => "10",
                _ => "8"
            };

            return settings;
        }

        private string GenerateMPEGEncoderSpeed()
        {
            return SliderEncoderPreset.Value switch
            {
                0 => "placebo",
                1 => "veryslow",
                2 => "slower",
                3 => "slow",
                4 => "medium",
                5 => "fast",
                6 => "faster",
                7 => "veryfast",
                8 => "superfast",
                9 => "ultrafast",
                _ => "medium",
            };
        }

        private string GenerateQuickSyncEncoderSpeed()
        {
            return SliderEncoderPreset.Value switch
            {
                0 => "best",
                1 => "higher",
                2 => "high",
                3 => "balanced",
                4 => "fast",
                5 => "faster",
                6 => "fastest",
                _ => "balanced",
            };
        }

        private string GenerateNVENCEncoderSpeed()
        {
            return SliderEncoderPreset.Value switch
            {
                0 => "quality",
                1 => "default",
                2 => "performance",
                _ => "default"
            };
        }

        private string GenerateKeyFrameInerval()
        {
            int seconds = 10;

            // Custom Framerate
            if (ComboBoxVideoFrameRate.SelectedIndex != 0)
            {
                try
                {
                    string selectedFramerate = ComboBoxVideoFrameRate.Text;
                    if (ComboBoxVideoFrameRate.SelectedIndex == 6) { selectedFramerate = "24"; }
                    if (ComboBoxVideoFrameRate.SelectedIndex == 9) { selectedFramerate = "30"; }
                    if (ComboBoxVideoFrameRate.SelectedIndex == 13) { selectedFramerate = "60"; }
                    int frames = int.Parse(selectedFramerate) * seconds;
                    return frames.ToString();
                } catch { }
            }

            // Framerate of Video if it's not VFR and MediaInfo Detected it
            if (!videoDB.MIIsVFR && !string.IsNullOrEmpty(videoDB.MIFramerate))
            {  
                try
                {
                    int framerate = int.Parse(videoDB.MIFramerate);
                    int frames = framerate * seconds;
                    return frames.ToString();
                } catch { }
            }

            return "240";
        }

        private string GenerateFFmpegColorSpace()
        {
            string settings = "-pix_fmt yuv4";

            if (ComboBoxColorFormat.SelectedIndex == 0)
            {
                settings += "20p";
            }
            else if (ComboBoxColorFormat.SelectedIndex == 1)
            {
                settings += "22p";
            }
            else if (ComboBoxColorFormat.SelectedIndex == 2)
            {
                settings += "44p";
            }

            if (ComboBoxVideoEncoder.SelectedIndex is (int) Video.Encoder.QSVAV1 or (int) Video.Encoder.X264)
            {
                if (ComboBoxVideoBitDepthLimited.SelectedIndex == 1)
                {
                    settings += "10le -strict -1";
                }
            }
            else
            {
                if (ComboBoxVideoBitDepth.SelectedIndex == 1)
                {
                    settings += "10le -strict -1";
                }
                else if (ComboBoxVideoBitDepth.SelectedIndex == 2)
                {
                    settings += "12le -strict -1";
                }
            }

            return settings;
        }

        private string GenerateMKVMergeHDRCommand()
        {
            string settings = " ";
            if (CheckBoxVideoHDR.IsChecked == true)
            {
                settings = "";
                if (CheckBoxMKVMergeMasteringDisplay.IsChecked == true)
                {
                    // --chromaticity-coordinates TID:red-x,red-y,green-x,green-y,blue-x,blue-y
                    settings += " --chromaticity-coordinates 0:" +
                        TextBoxMKVMergeMasteringRx.Text + "," +
                        TextBoxMKVMergeMasteringRy.Text + "," +
                        TextBoxMKVMergeMasteringGx.Text + "," +
                        TextBoxMKVMergeMasteringGy.Text + "," +
                        TextBoxMKVMergeMasteringBx.Text + "," +
                        TextBoxMKVMergeMasteringBy.Text;
                }
                if (CheckBoxMKVMergeWhiteMasteringDisplay.IsChecked == true)
                {
                    // --white-colour-coordinates TID:x,y
                    settings += " --white-colour-coordinates 0:" +
                        TextBoxMKVMergeMasteringWPx.Text + "," +
                        TextBoxMKVMergeMasteringWPy.Text;
                }
                if (CheckBoxMKVMergeLuminance.IsChecked == true)
                {
                    // --max-luminance TID:float
                    // --min-luminance TID:float
                    settings += " --max-luminance 0:" + TextBoxMKVMergeMasteringLMax.Text;
                    settings += " --min-luminance 0:" + TextBoxMKVMergeMasteringLMin.Text;
                }
                if (CheckBoxMKVMergeMaxContentLight.IsChecked == true)
                {
                    // --max-content-light TID:n
                    settings += " --max-content-light 0:" + TextBoxMKVMergeMaxContentLight.Text;
                }
                if (CheckBoxMKVMergeMaxFrameLight.IsChecked == true)
                {
                    // --max-frame-light TID:n
                    settings += " --max-frame-light 0:" + TextBoxMKVMergeMaxFrameLight.Text;
                }
                if (ComboBoxMKVMergeColorPrimaries.SelectedIndex != 2)
                {
                    // --colour-primaries TID:n
                    settings += " --colour-primaries 0:" + ComboBoxMKVMergeColorPrimaries.SelectedIndex.ToString();
                }
                if (ComboBoxMKVMergeColorTransfer.SelectedIndex != 2)
                {
                    // --colour-transfer-characteristics TID:n
                    settings += " --colour-transfer-characteristics 0:" + ComboBoxMKVMergeColorTransfer.SelectedIndex.ToString();
                }
                if (ComboBoxMKVMergeColorMatrix.SelectedIndex != 2)
                {
                    // --colour-matrix-coefficients TID:n
                    settings += " --colour-matrix-coefficients 0:" + ComboBoxMKVMergeColorMatrix.SelectedIndex.ToString();
                }
            }
            return settings;
        }
        #endregion

        #region Main Entry
        private async void PreStart()
        {
            // Creates new Cancellation Token
            cancellationTokenSource = new CancellationTokenSource();

            try
            {
                await MainStartAsync(cancellationTokenSource.Token);
            }
            catch (OperationCanceledException) { }

            // Dispose Cancellation Source after Main Function finished
            cancellationTokenSource.Dispose();
        }

        private async Task MainStartAsync(CancellationToken _cancelToken)
        {
            QueueParallel = ToggleSwitchQueueParallel.IsOn;
            // Sets amount of Workers
            int WorkerCountQueue = 1;
            int WorkerCountElement = int.Parse(ComboBoxWorkerCount.Text);

            if (settingsDB.OverrideWorkerCount)
            {
                WorkerCountElement = int.Parse(TextBoxWorkerCount.Text);
            }

            // If user wants to encode the queue in parallel,
            // it will set the worker count to 1 and the "outer"
            // SemaphoreSlim will be set to the original worker count
            if (QueueParallel)
            {
                WorkerCountQueue = WorkerCountElement;
                WorkerCountElement = 1;
            }

            // Starts Timer for Taskbar Progress Indicator
            System.Timers.Timer taskBarTimer = new();
            Dispatcher.Invoke(() => TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Normal);
            taskBarTimer.Elapsed += (sender, e) => { UpdateTaskbarProgress(); };
            taskBarTimer.Interval = 3000; // every 3s
            taskBarTimer.Start();

            // Starts Timer for Auto Pause Resume functionality
            System.Timers.Timer pauseResumeTimer = new();
            if (settingsDB.AutoResumePause)
            {
                pauseResumeTimer.Elapsed += (sender, e) => { AutoPauseResume(); };
                pauseResumeTimer.Interval = 20000; // check every 10s
                pauseResumeTimer.Start();
            }

            using SemaphoreSlim concurrencySemaphore = new(WorkerCountQueue);
            // Creates a tasks list
            List<Task> tasks = new();

            foreach (Queue.QueueElement queueElement in ListBoxQueue.Items)
            {
                await concurrencySemaphore.WaitAsync(_cancelToken);
                Task task = Task.Run(async () =>
                {
                    try
                    {
                        // Create Output Directory
                        try {  Directory.CreateDirectory(Path.GetDirectoryName(queueElement.VideoDB.OutputPath)); }  catch { }

                        // Create Temp Directory
                        Directory.CreateDirectory(Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier));

                        Global.Logger("==========================================================", queueElement.Output + ".log");
                        Global.Logger("INFO  - Started Async Task - UID: " + queueElement.UniqueIdentifier, queueElement.Output + ".log");
                        Global.Logger("INFO  - Input: " + queueElement.Input, queueElement.Output + ".log");
                        Global.Logger("INFO  - Output: " + queueElement.Output, queueElement.Output + ".log");
                        Global.Logger("INFO  - Temp Folder: " + Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier), queueElement.Output + ".log");
                        Global.Logger("==========================================================", queueElement.Output + ".log");

                        Audio.EncodeAudio encodeAudio = new();
                        Subtitle.ExtractSubtitles extractSubtitles = new();
                        Video.VideoSplitter videoSplitter = new();
                        Video.VideoEncode videoEncoder = new();
                        Video.VideoMuxer videoMuxer = new();

                        // Get Framecount
                        await Task.Run(() => queueElement.GetFrameCount());

                        // Subtitle Extraction
                        await Task.Run(() => extractSubtitles.Extract(queueElement, _cancelToken), _cancelToken);

                        List<string> VideoChunks = new();

                        // Chunking
                        if (QueueParallel)
                        {
                            VideoChunks.Add(queueElement.VideoDB.InputPath);
                            Global.Logger("WARN  - Queue is being processed in Parallel", queueElement.Output + ".log");
                        }
                        else
                        {
                            await Task.Run(() => videoSplitter.Split(queueElement, _cancelToken), _cancelToken);

                            if (queueElement.ChunkingMethod == 0 || queueElement.Preset.TargetVMAF)
                            {
                                // Equal Chunking
                                IOrderedEnumerable<string> sortedChunks = Directory.GetFiles(Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier, "Chunks"), "*.mkv", SearchOption.TopDirectoryOnly).OrderBy(f => f);
                                foreach (string file in sortedChunks)
                                {
                                    VideoChunks.Add(file);
                                    Global.Logger("TRACE - Equal Chunking VideoChunks Add " + file, queueElement.Output + ".log");
                                }
                            }
                            else
                            {
                                // Scene Detect
                                if (File.Exists(Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier, "splits.txt")))
                                {
                                    VideoChunks = File.ReadAllLines(Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier, "splits.txt")).ToList();
                                    Global.Logger("TRACE - SceneDetect VideoChunks Add " + VideoChunks, queueElement.Output + ".log");
                                }
                            }
                        }

                        if (VideoChunks.Count == 0)
                        {
                            queueElement.Status = "Error: No Video Chunk found";
                            Global.Logger("FATAL - Error: No Video Chunk found", queueElement.Output + ".log");
                        }
                        else
                        {
                            // Audio Encoding
                            await Task.Run(() => encodeAudio.Encode(queueElement, _cancelToken), _cancelToken);

                            // Extract VFR Timestamps
                            await Task.Run(() => queueElement.GetVFRTimeStamps(), _cancelToken);

                            // Start timer for eta / fps calculation
                            DateTime startTime = DateTime.Now - queueElement.TimeEncoded;
                            System.Timers.Timer aTimer = new();
                            aTimer.Elapsed += (sender, e) => { UpdateProgressBar(queueElement, startTime); };
                            aTimer.Interval = 1000;
                            aTimer.Start();

                            // Video Encoding
                            await Task.Run(() => videoEncoder.Encode(WorkerCountElement, VideoChunks, queueElement, QueueParallel, settingsDB.PriorityNormal, settingsDB, _cancelToken), _cancelToken);

                            // Stop timer for eta / fps calculation
                            aTimer.Stop();

                            // Video Muxing
                            await Task.Run(() => videoMuxer.Concat(queueElement), _cancelToken);

                            // Temp File Deletion
                            await Task.Run(() => DeleteTempFiles(queueElement, startTime), _cancelToken);

                            // Save Queue States (e.g. Chunk Progress)
                            SaveQueueElementState(queueElement, VideoChunks);
                        }
                    }
                    catch (TaskCanceledException) { }
                    finally
                    {
                        concurrencySemaphore.Release();
                    }
                }, _cancelToken);

                tasks.Add(task);
            }
            try
            {
                await Task.WhenAll(tasks.ToArray());
            }
            catch (OperationCanceledException) { }

            ProgramState = 0;
            ImageStartStop.Source = new BitmapImage(new Uri(@"/NotEnoughAV1Encodes;component/resources/img/start.png", UriKind.Relative));
            LabelStartPauseButton.Content = LocalizedStrings.Instance["LabelStartPauseButton"];
            ButtonAddToQueue.IsEnabled = true;
            ButtonRemoveSelectedQueueItem.IsEnabled = true;
            ButtonEditSelectedItem.IsEnabled = true;
            ButtonClearQueue.IsEnabled = true;
            ComboBoxSortQueueBy.IsEnabled = true;

            // Stop Timer for Auto Pause Resume functionality
            if (settingsDB.AutoResumePause)
            {
                pauseResumeTimer.Stop();
            }

            // Stop TaskbarItem Progressbar
            taskBarTimer.Stop();
            Dispatcher.Invoke(() => TaskbarItemInfo.ProgressValue = 1.0);
            Dispatcher.Invoke(() => TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Paused);

            // Remove Tasks from Queue if enabled in settings
            if (settingsDB.AutoClearQueue)
            {
                List<Queue.QueueElement> queueItems = new();
                foreach(Queue.QueueElement queueElement in ListBoxQueue.Items)
                {
                    if (queueElement == null) continue;
                    // Skip Item if there was some error during encoding / muxing
                    if (queueElement.Error == true) continue;
                    // Check if Outfile exists
                    if (!File.Exists(queueElement.VideoDB.OutputPath)) continue;
                    // Check Outfilesize
                    FileInfo videoOutput = new(queueElement.VideoDB.OutputPath);
                    if (videoOutput.Length <= 50000) continue;

                    queueItems.Add(queueElement);
                }
                foreach(Queue.QueueElement queueElement in queueItems)
                {
                    ListBoxQueue.Items.Remove(queueElement);
                    try
                    {
                        File.Delete(Path.Combine(Global.AppData, "NEAV1E", "Queue", queueElement.VideoDB.InputFileName + "_" + queueElement.UniqueIdentifier + ".json"));
                    }
                    catch { }
                }
            }

            Shutdown();
        }
        #endregion

        #region Progressbar
        private static void UpdateProgressBar(Queue.QueueElement queueElement, DateTime startTime)
        {
            queueElement.TimeEncoded = DateTime.Now - startTime;
            long encodedFrames = 0;
            long encodedFramesSecondPass = 0;

            foreach (Queue.ChunkProgress progress in queueElement.ChunkProgress)
            {
                try
                {
                    encodedFrames += progress.Progress;
                }
                catch { }
            }

            // Progress 1-Pass encoding or 1st Pass of 2-Pass encoding
            queueElement.Progress = Convert.ToDouble(encodedFrames);
            
            if (queueElement.Passes == 2)
            {
                // 2 Pass encoding
                foreach (Queue.ChunkProgress progress in queueElement.ChunkProgress)
                {
                    try
                    {
                        encodedFramesSecondPass += progress.ProgressSecondPass;
                    }
                    catch { }
                }

                // Progress 2nd-Pass of 2-Pass Encoding
                queueElement.ProgressSecondPass = Convert.ToDouble(encodedFramesSecondPass);

                string estimatedFPS1stPass = "";
                string estimatedFPS2ndPass = "";
                string estimatedTime1stPass = "";
                string estimatedTime2ndPass = "";

                if (encodedFrames != queueElement.FrameCount)
                {
                    estimatedFPS1stPass = "   -  ~" + Math.Round(encodedFrames / queueElement.TimeEncoded.TotalSeconds, 2).ToString("0.00") + "fps";
                    estimatedTime1stPass = "   -  ~" + Math.Round(((queueElement.TimeEncoded.TotalSeconds / encodedFrames) * (queueElement.FrameCount - encodedFrames)) / 60, MidpointRounding.ToEven) + LocalizedStrings.Instance["QueueMinLeft"];
                }

                if(encodedFramesSecondPass != queueElement.FrameCount)
                {
                    estimatedFPS2ndPass = "   -  ~" + Math.Round(encodedFramesSecondPass / queueElement.TimeEncoded.TotalSeconds, 2).ToString("0.00") + "fps";
                    estimatedTime2ndPass = "   -  ~" + Math.Round(((queueElement.TimeEncoded.TotalSeconds / encodedFramesSecondPass) * (queueElement.FrameCount - encodedFramesSecondPass)) / 60, MidpointRounding.ToEven) + LocalizedStrings.Instance["QueueMinLeft"];
                }
                
                queueElement.Status = LocalizedStrings.Instance["Queue1stPass"] + " " + ((decimal)encodedFrames / queueElement.FrameCount).ToString("00.00%") + estimatedFPS1stPass + estimatedTime1stPass + " - " + LocalizedStrings.Instance["Queue2ndPass"] + " " + ((decimal)encodedFramesSecondPass / queueElement.FrameCount).ToString("00.00%") + estimatedFPS2ndPass + estimatedTime2ndPass;
            }
            else
            {
                // 1 Pass encoding
                string estimatedFPS = "   -  ~" + Math.Round(encodedFrames / queueElement.TimeEncoded.TotalSeconds, 2).ToString("0.00") + "fps";
                string estimatedTime = "   -  ~" + Math.Round(((queueElement.TimeEncoded.TotalSeconds / encodedFrames) * (queueElement.FrameCount - encodedFrames)) / 60, MidpointRounding.ToEven) + LocalizedStrings.Instance["QueueMinLeft"];

                queueElement.Status = "Encoded: " + ((decimal)encodedFrames / queueElement.FrameCount).ToString("00.00%") + estimatedFPS + estimatedTime;
            }

            try
            {
                File.WriteAllText(Path.Combine(Global.AppData, "NEAV1E", "Queue", queueElement.VideoDB.InputFileName + "_" + queueElement.UniqueIdentifier + ".json"), JsonConvert.SerializeObject(queueElement, Formatting.Indented));
            }
            catch { }
        }


        private void UpdateTaskbarProgress()
        {
            double totalFrames = 0;
            double totalFramesEncoded = 0;
            System.Windows.Controls.ItemCollection queueList = ListBoxQueue.Items;

            // Calculte Total Framecount
            try
            {
                foreach (Queue.QueueElement queueElement in queueList)
                {
                    totalFrames += queueElement.FrameCount;
                    totalFramesEncoded += queueElement.Progress;
                    if (queueElement.Passes == 2)
                    {
                        // Double Framecount of that queue element for two pass encoding
                        totalFrames += queueElement.FrameCount;
                        totalFramesEncoded += queueElement.ProgressSecondPass;
                    }
                }
            }
            catch { }

            // Dividing by 0 is always great, so we are going to skip it
            if (totalFrames == 0 || totalFramesEncoded == 0) return;

            try
            {
                Dispatcher.Invoke(() => TaskbarItemInfo.ProgressValue = totalFramesEncoded / totalFrames);
            }
            catch { }
        }
        #endregion
    }
}
