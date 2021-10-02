﻿using System;
using System.Windows;
using MahApps.Metro.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Newtonsoft.Json;
using System.Threading.Tasks;
using System.IO;
using System.Text;
using System.Threading;
using System.Diagnostics;
using ControlzEx.Theming;
using System.Windows.Media;
using System.Linq;

namespace NotEnoughAV1Encodes
{
    public partial class MainWindow : MetroWindow
    {
        private bool QueueParallel;
        private SettingsDB settingsDB = new();
        private Video.VideoDB videoDB = new();
        private int ProgramState;
        private CancellationTokenSource cancellationTokenSource;
        public Settings PresetSettings = new();

        public ObservableCollection<Queue.QueueElement> QueueList { get; set; } = new();

        public MainWindow()
        {
            InitializeComponent();
            Initialize();
            DataContext = PresetSettings;
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
            try { settingsDB = JsonConvert.DeserializeObject<SettingsDB>(File.ReadAllText(Path.Combine(Global.AppData, "NEAV1E", "settings.json"))); } catch { }

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
        private void ButtonCancelEncode_Click(object sender, RoutedEventArgs e)
        {
            cancellationTokenSource.Cancel();
            ImageStartStop.Source = new BitmapImage(new Uri(@"/NotEnoughAV1Encodes;component/resources/img/start.png", UriKind.Relative));
        }
        private void ButtonProgramSettings_Click(object sender, RoutedEventArgs e)
        {
            Views.ProgramSettings programSettings = new(settingsDB);
            programSettings.ShowDialog();
            settingsDB.DeleteTempFiles = programSettings.DeleteTempFiles;
            settingsDB.ShutdownAfterEncode = programSettings.ShutdownAfterEncode;
            settingsDB.BaseTheme = programSettings.BaseTheme;
            settingsDB.AccentTheme = programSettings.AccentTheme;
            settingsDB.Theme = programSettings.Theme;
            settingsDB.BGImage = programSettings.BGImage;
            settingsDB.OverrideWorkerCount = programSettings.OverrideWorkerCount;

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
            if (ListBoxQueue.SelectedItem != null)
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

        private void ButtonOpenSource_Click(object sender, RoutedEventArgs e)
        {
            Views.OpenSource openSource = new(settingsDB.Theme);
            openSource.ShowDialog();
            if (openSource.Quit)
            {
                videoDB.InputPath = openSource.Path;
                videoDB.ParseMediaInfo();
                ListBoxAudioTracks.Items.Clear();
                ListBoxAudioTracks.ItemsSource = videoDB.AudioTracks;
                LabelVideoSource.Content = videoDB.InputPath;
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
            }
        }

        private void ButtonSetDestination_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog saveVideoFileDialog = new()
            {
                Filter = "MKV Video|*.mkv|WebM Video|*.webm|MP4 Video|*.mp4"
            };

            if (saveVideoFileDialog.ShowDialog() == true)
            {
                videoDB.OutputPath = saveVideoFileDialog.FileName;
                LabelVideoDestination.Content = videoDB.OutputPath;
                videoDB.OutputFileName = Path.GetFileName(videoDB.OutputPath);
            }
        }

        private void ButtonStartStop_Click(object sender, RoutedEventArgs e)
        {
            if (ListBoxQueue.Items.Count != 0)
            {
                if (ProgramState is 0 or 2)
                {
                    ImageStartStop.Source = new BitmapImage(new Uri(@"/NotEnoughAV1Encodes;component/resources/img/pause.png", UriKind.Relative));
                    LabelStartPauseButton.Content = "Pause";

                    // Main Start
                    if (ProgramState is 0)
                    {
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
                    LabelStartPauseButton.Content = "Resume";

                    // Pause all PIDs
                    foreach (int pid in Global.LaunchedPIDs)
                    {
                        Suspend.SuspendProcessTree(pid);
                    }
                }
            }
            else
            {
                // To-Do: Error Meldung
            }
        }

        private void ButtonAddToQueue_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(videoDB.InputPath))
            {
                // Throw Error
                return;
            }

            if (string.IsNullOrEmpty(videoDB.OutputPath))
            {
                // Throw Error
                return;
            }

            Queue.QueueElement queueElement = new();
            Audio.CommandGenerator commandgenerator = new();

            queueElement.VideoCommand = GenerateEncoderCommand();
            queueElement.AudioCommand = commandgenerator.Generate(ListBoxAudioTracks.Items);
            queueElement.FrameCount = videoDB.MIFrameCount;
            queueElement.EncodingMethod = ComboBoxVideoEncoder.SelectedIndex;
            queueElement.ChunkingMethod = ComboBoxChunkingMethod.SelectedIndex;
            queueElement.ReencodeMethod = ComboBoxReencodeMethod.SelectedIndex;
            queueElement.Passes = CheckBoxTwoPassEncoding.IsChecked == true ? 2 : 1;
            queueElement.ChunkLength = int.Parse(TextBoxChunkLength.Text);
            queueElement.PySceneDetectThreshold = float.Parse(TextBoxPySceneDetectThreshold.Text);
            queueElement.VFR = CheckBoxVideoVFR.IsChecked == true;
            queueElement.Preset = PresetSettings;
            queueElement.VideoDB = videoDB;

            // Double the framecount for two pass encoding
            if (queueElement.Passes == 2)
            {
                queueElement.FrameCount = videoDB.MIFrameCount + videoDB.MIFrameCount;
            }

            // Generate a random identifier to avoid filesystem conflicts
            const string src = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
            StringBuilder identifier = new();
            Random RNG = new();
            for (int i = 0; i < 15; i++)
            {
                identifier.Append(src[RNG.Next(0, src.Length)]);
            }

            queueElement.UniqueIdentifier = identifier.ToString();

            // Add to Queue
            ListBoxQueue.Items.Add(queueElement);

            Directory.CreateDirectory(Path.Combine(Global.AppData, "NEAV1E", "Queue"));

            // Save as JSON
            File.WriteAllText(Path.Combine(Global.AppData, "NEAV1E", "Queue", videoDB.InputFileName + "_" + identifier + ".json"), JsonConvert.SerializeObject(queueElement, Formatting.Indented));

            Dispatcher.BeginInvoke((Action)(() => TabControl.SelectedIndex = 3));
        }

        private void ButtonSavePreset_Click(object sender, RoutedEventArgs e)
        {
            Views.SavePresetDialog savePresetDialog = new(settingsDB.Theme);
            savePresetDialog.ShowDialog();
            if (savePresetDialog.Quit)
            {
                Debug.WriteLine("Save: " + savePresetDialog.PresetName);
                Directory.CreateDirectory(Path.Combine(Global.AppData, "NEAV1E", "Presets"));
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
        private void ButtonEditSelectedItem_Click(object sender, RoutedEventArgs e)
        {
            if (ListBoxQueue.SelectedItem != null)
            {
                Queue.QueueElement tmp = (Queue.QueueElement)ListBoxQueue.SelectedItem;
                PresetSettings = tmp.Preset;
                DataContext = PresetSettings;
                videoDB = tmp.VideoDB;
                ListBoxAudioTracks.Items.Clear();
                ListBoxAudioTracks.ItemsSource = videoDB.AudioTracks;
                LabelVideoSource.Content = videoDB.InputPath;
                LabelVideoDestination.Content = videoDB.OutputPath;
                LabelVideoLength.Content = videoDB.MIDuration;
                LabelVideoResolution.Content = videoDB.MIWidth + "x" + videoDB.MIHeight;
                LabelVideoColorFomat.Content = videoDB.MIChromaSubsampling;

                ComboBoxChunkingMethod.SelectedIndex = tmp.ChunkingMethod;
                ComboBoxReencodeMethod.SelectedIndex = tmp.ReencodeMethod;
                CheckBoxTwoPassEncoding.IsChecked = tmp.Passes == 2;
                TextBoxChunkLength.Text = tmp.ChunkLength.ToString();
                TextBoxPySceneDetectThreshold.Text = tmp.PySceneDetectThreshold.ToString();


                try
                {
                    File.Delete(Path.Combine(Global.AppData, "NEAV1E", "Queue", tmp.VideoDB.InputFileName + "_" + tmp.UniqueIdentifier + ".json"));
                }
                catch { }

                ListBoxQueue.Items.Remove(ListBoxQueue.SelectedItem);
            }
        }
        #endregion

        #region UI Functions
        private void ComboBoxPresets_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            try
            {
                PresetSettings = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(Path.Combine(Global.AppData, "NEAV1E", "Presets", ComboBoxPresets.SelectedItem.ToString() + ".json")));
                DataContext = PresetSettings;
            }
            catch { }
        }
        private void ComboBoxVideoEncoder_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (TextBoxMaxBitrate != null)
            {
                if (ComboBoxVideoEncoder.SelectedIndex is 0 or 5)
                {
                    //aom ffmpeg
                    TextBoxMaxBitrate.Visibility = Visibility.Visible;
                    TextBoxMinBitrate.Visibility = Visibility.Visible;
                    SliderEncoderPreset.Maximum = 9;
                    SliderEncoderPreset.Value = 4;
                    SliderQuality.Maximum = 63;
                    SliderQuality.Value = 25;
                    CheckBoxTwoPassEncoding.IsEnabled = true;
                }
                else if (ComboBoxVideoEncoder.SelectedIndex is 1 or 6)
                {
                    //rav1e ffmpeg
                    TextBoxMaxBitrate.Visibility = Visibility.Collapsed;
                    TextBoxMinBitrate.Visibility = Visibility.Collapsed;
                    ComboBoxQualityMode.SelectedIndex = 0;
                    SliderEncoderPreset.Maximum = 10;
                    SliderEncoderPreset.Value = 5;
                    SliderQuality.Maximum = 255;
                    SliderQuality.Value = 80;
                    CheckBoxTwoPassEncoding.IsChecked = false;
                    CheckBoxTwoPassEncoding.IsEnabled = false;
                }
                else if (ComboBoxVideoEncoder.SelectedIndex is 2 or 7)
                {
                    //svt-av1 ffmpeg
                    TextBoxMaxBitrate.Visibility = Visibility.Collapsed;
                    TextBoxMinBitrate.Visibility = Visibility.Collapsed;
                    ComboBoxQualityMode.SelectedIndex = 0;
                    SliderEncoderPreset.Maximum = 8;
                    SliderEncoderPreset.Value = 6;
                    SliderQuality.Maximum = 63;
                    SliderQuality.Value = 40;
                    CheckBoxTwoPassEncoding.IsEnabled = true;
                    CheckBoxTwoPassEncoding.IsChecked = false;
                }
                else if (ComboBoxVideoEncoder.SelectedIndex == 3)
                {
                    //vpx-vp9 ffmpeg
                    TextBoxMaxBitrate.Visibility = Visibility.Visible;
                    TextBoxMinBitrate.Visibility = Visibility.Visible;
                    SliderEncoderPreset.Maximum = 9;
                    SliderEncoderPreset.Value = 4;
                    SliderQuality.Maximum = 63;
                    SliderQuality.Value = 25;
                    CheckBoxTwoPassEncoding.IsEnabled = true;
                }
            }
        }
        private void CheckBoxTwoPassEncoding_Checked(object sender, RoutedEventArgs e)
        {
            if (ComboBoxVideoEncoder.SelectedIndex is 2 or 7 && ComboBoxQualityMode.SelectedIndex == 0 && CheckBoxTwoPassEncoding.IsChecked == true)
            {
                CheckBoxTwoPassEncoding.IsChecked = false;
            }
        }
        private void ComboBoxQualityMode_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (TextBoxAVGBitrate != null)
            {
                if (ComboBoxVideoEncoder.SelectedIndex is 1 or 2 or 6 or 7)
                {
                    if (ComboBoxQualityMode.SelectedIndex is 1 or 3)
                    {
                        ComboBoxQualityMode.SelectedIndex = 0;
                        MessageBox.Show("NEAV1E currently only supports Constant Quality or Bitrate Mode (rav1e / svt-av1)");
                        return;
                    }
                    if(CheckBoxTwoPassEncoding.IsChecked == true && ComboBoxVideoEncoder.SelectedIndex is 2 or 7 && ComboBoxQualityMode.SelectedIndex == 0)
                    {
                        CheckBoxTwoPassEncoding.IsChecked = false;
                    }
                }
                if (ComboBoxVideoEncoder.SelectedIndex is 5)
                {
                    if (ComboBoxQualityMode.SelectedIndex is 3)
                    {
                        ComboBoxQualityMode.SelectedIndex = 0;
                        MessageBox.Show("NEAV1E currently does not support Constrained Bitrate (aomenc)");
                        return;
                    }
                }
                if (ComboBoxQualityMode.SelectedIndex == 0)
                {
                    SliderQuality.IsEnabled = true;
                    TextBoxAVGBitrate.IsEnabled = false;
                    TextBoxMaxBitrate.IsEnabled = false;
                    TextBoxMinBitrate.IsEnabled = false;
                }
                else if (ComboBoxQualityMode.SelectedIndex == 1)
                {
                    SliderQuality.IsEnabled = true;
                    TextBoxAVGBitrate.IsEnabled = false;
                    TextBoxMaxBitrate.IsEnabled = true;
                    TextBoxMinBitrate.IsEnabled = false;
                }
                else if (ComboBoxQualityMode.SelectedIndex == 2)
                {
                    SliderQuality.IsEnabled = false;
                    TextBoxAVGBitrate.IsEnabled = true;
                    TextBoxMaxBitrate.IsEnabled = false;
                    TextBoxMinBitrate.IsEnabled = false;
                }
                else if (ComboBoxQualityMode.SelectedIndex == 3)
                {
                    SliderQuality.IsEnabled = false;
                    TextBoxAVGBitrate.IsEnabled = true;
                    TextBoxMaxBitrate.IsEnabled = true;
                    TextBoxMinBitrate.IsEnabled = true;
                }
            }
        }
        private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
        {
            // Validates that the TextBox Input are only numbers
            Regex regex = new("[^0-9]+");
            e.Handled = regex.IsMatch(e.Text);
        }
        #endregion

        #region Small Functions
        private void LoadSettings()
        {
            if (settingsDB.OverrideWorkerCount)
            {
                ComboBoxWorkerCount.Visibility = Visibility.Hidden;
                TextBoxWorkerCount.Visibility = Visibility.Visible;
            }
            else
            {
                ComboBoxWorkerCount.Visibility = Visibility.Visible;
                TextBoxWorkerCount.Visibility = Visibility.Hidden;
            }

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
                }
                else
                {
                    bgImage.Source = null;
                }
            }
            catch { }
        }

        private void Shutdown()
        {
            if (settingsDB.ShutdownAfterEncode)
            {
                Process.Start("shutdown.exe", "/s /t 0");
            }
        }
        private void DeleteTempFiles(Queue.QueueElement queueElement)
        {
            if (settingsDB.DeleteTempFiles)
            {
                if (File.Exists(queueElement.VideoDB.OutputPath))
                {
                    FileInfo _videoOutput = new(queueElement.VideoDB.OutputPath);
                    if (_videoOutput.Length >= 50000)
                    {
                        try
                        {
                            DirectoryInfo tmp = new(Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier));
                            tmp.Delete(true);
                        }
                        catch (Exception e)
                        {
                            MessageBox.Show(e.Message, "Error Deleting Temp Files", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                    else
                    {
                        queueElement.Status = "Potential Muxing Error";
                    }
                }
                else
                {
                    queueElement.Status = "Error: No Output detected";
                }
            }
        }
        #endregion

        #region Encoder Settings
        private string GenerateEncoderCommand()
        {
            string _settings = GenerateFFmpegColorSpace() + " " + GenerateFFmpegFramerate() + " ";
            if (ComboBoxVideoEncoder.SelectedIndex == 0)
            {
                return _settings + GenerateAomFFmpegCommand();
            }
            else if (ComboBoxVideoEncoder.SelectedIndex == 1)
            {
                return _settings + GenerateRav1eFFmpegCommand();
            }
            else if (ComboBoxVideoEncoder.SelectedIndex == 2)
            {
                return _settings + GenerateSvtAV1FFmpegCommand();
            }
            else if (ComboBoxVideoEncoder.SelectedIndex == 3)
            {
                return _settings + GenerateVpxVP9Command();
            }
            else if (ComboBoxVideoEncoder.SelectedIndex == 5)
            {
                return _settings + GenerateAomencCommand();
            }
            else if (ComboBoxVideoEncoder.SelectedIndex == 6)
            {
                return _settings + GenerateRav1eCommand();
            }
            else if (ComboBoxVideoEncoder.SelectedIndex == 7)
            {
                return _settings + GenerateSvtAV1Command();
            }

            return "";
        }

        private string GenerateAomFFmpegCommand()
        {
            string _settings = "-c:v libaom-av1";

            if (ComboBoxQualityMode.SelectedIndex == 0)
            {
                _settings += " -crf " + SliderQuality.Value + " -b:v 0";
            }
            else if (ComboBoxQualityMode.SelectedIndex == 1)
            {
                _settings += " -crf " + SliderQuality.Value + " -b:v " + TextBoxMaxBitrate.Text + "k";
            }
            else if (ComboBoxQualityMode.SelectedIndex == 2)
            {
                _settings += " -b:v " + TextBoxMinBitrate.Text + "k";
            }
            else if (ComboBoxQualityMode.SelectedIndex == 3)
            {
                _settings += " -minrate " + TextBoxMinBitrate.Text + "k -b:v " + TextBoxAVGBitrate.Text + "k -maxrate " + TextBoxMaxBitrate.Text + "k";
            }

            _settings += " -cpu-used " + SliderEncoderPreset.Value;

            return _settings;
        }
        
        private string GenerateRav1eFFmpegCommand()
        {
            string _settings = "-c:v librav1e";

            if (ComboBoxQualityMode.SelectedIndex == 0)
            {
                _settings += " -qp " + SliderQuality.Value;
            }
            else if (ComboBoxQualityMode.SelectedIndex == 2)
            {
                _settings += " -b:v " + TextBoxAVGBitrate.Text + "k";
            }

            _settings += " -speed " + SliderEncoderPreset.Value;

            return _settings;
        }

        private string GenerateSvtAV1FFmpegCommand()
        {
            string _settings = "-c:v libsvtav1";

            if (ComboBoxQualityMode.SelectedIndex == 0)
            {
                _settings += " -rc 0 -qp " + SliderQuality.Value;
            }
            else if (ComboBoxQualityMode.SelectedIndex == 2)
            {
                _settings += " -rc 1 -b:v " + TextBoxAVGBitrate.Text + "k";
            }

            _settings += " -preset " + SliderEncoderPreset.Value;

            return _settings;
        }

        private string GenerateVpxVP9Command()
        {
            string _settings = "-c:v libvpx-vp9";

            if (ComboBoxQualityMode.SelectedIndex == 0)
            {
                _settings += " -crf " + SliderQuality.Value + " -b:v 0";
            }
            else if (ComboBoxQualityMode.SelectedIndex == 1)
            {
                _settings += " -crf " + SliderQuality.Value + " -b:v " + TextBoxMaxBitrate.Text + "k";
            }
            else if (ComboBoxQualityMode.SelectedIndex == 2)
            {
                _settings += " -b:v " + TextBoxMinBitrate.Text + "k";
            }
            else if (ComboBoxQualityMode.SelectedIndex == 3)
            {
                _settings += " -minrate " + TextBoxMinBitrate.Text + "k -b:v " + TextBoxAVGBitrate.Text + "k -maxrate " + TextBoxMaxBitrate.Text + "k";
            }

            _settings += " -cpu-used " + SliderEncoderPreset.Value;

            return _settings;
        }

        private string GenerateAomencCommand()
        {
            string _settings = "-f yuv4mpegpipe - | ";

            _settings += "\"" + Path.Combine(Directory.GetCurrentDirectory(), "Apps", "aomenc", "aomenc.exe") + "\" -";

            if (ComboBoxQualityMode.SelectedIndex == 0)
            {
                _settings += " --cq-level=" + SliderQuality.Value + " --end-usage=q";
            }
            else if (ComboBoxQualityMode.SelectedIndex == 1)
            {
                _settings += " --cq-level=" + SliderQuality.Value + " --target-bitrate=" + TextBoxMaxBitrate.Text + " --end-usage=cq";
            }
            else if (ComboBoxQualityMode.SelectedIndex == 2)
            {
                _settings += " --target-bitrate=" + TextBoxMinBitrate.Text + " --end-usage=vbr";
            }

            _settings += " --cpu-used=" + SliderEncoderPreset.Value;

            return _settings;
        }

        private string GenerateRav1eCommand()
        {
            string _settings = "-f yuv4mpegpipe - | ";

            _settings += "\"" + Path.Combine(Directory.GetCurrentDirectory(), "Apps", "rav1e", "rav1e.exe") + "\" -";

            if (ComboBoxQualityMode.SelectedIndex == 0)
            {
                _settings += " --quantizer " + SliderQuality.Value;
            }
            else if (ComboBoxQualityMode.SelectedIndex == 2)
            {
                _settings += " --bitrate " + TextBoxAVGBitrate.Text;
            }

            _settings += " --speed " + SliderEncoderPreset.Value;

            return _settings;
        }

        private string GenerateSvtAV1Command()
        {
            string _settings = "-nostdin -f yuv4mpegpipe - | ";

            _settings += "\"" + Path.Combine(Directory.GetCurrentDirectory(), "Apps", "svt-av1", "SvtAv1EncApp.exe") + "\" -i stdin";

            if (ComboBoxQualityMode.SelectedIndex == 0)
            {
                _settings += " --rc 0 --crf " + SliderQuality.Value;
            }
            else if (ComboBoxQualityMode.SelectedIndex == 2)
            {
                _settings += " --rc 1 --tbr " + TextBoxAVGBitrate.Text;
            }

            _settings += " --preset " + SliderEncoderPreset.Value;

            return _settings;
        }

        private string GenerateFFmpegColorSpace()
        {
            string _settings = "-pix_fmt yuv4";
            if (ComboBoxColorFormat.SelectedIndex == 0)
            {
                _settings += "20p";
            }
            else if (ComboBoxColorFormat.SelectedIndex == 1)
            {
                _settings += "22p";
            }
            else if (ComboBoxColorFormat.SelectedIndex == 2)
            {
                _settings += "44p";
            }
            if (ComboBoxVideoBitDepth.SelectedIndex == 1)
            {
                _settings += "10le -strict -1";
            }
            else if (ComboBoxVideoBitDepth.SelectedIndex == 2)
            {
                _settings += "12le -strict -1";
            }
            return _settings;
        }

        private string GenerateFFmpegFramerate()
        {
            string _settings = "";

            if (ComboBoxVideoFrameRate.SelectedIndex != 0)
            {
                _settings = "-vf fps=" + ComboBoxVideoFrameRate.Text;
                if (ComboBoxVideoFrameRate.SelectedIndex == 6) { _settings = "-vf fps=24000/1001"; }
                if (ComboBoxVideoFrameRate.SelectedIndex == 9) { _settings = "-vf fps=30000/1001"; }
                if (ComboBoxVideoFrameRate.SelectedIndex == 13) { _settings = "-vf fps=60000/1001"; }
            }

            return _settings;
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

            using SemaphoreSlim concurrencySemaphore = new(WorkerCountQueue);
            // Creates a tasks list
            List<Task> tasks = new();

            foreach (Queue.QueueElement queueElement in ListBoxQueue.Items)
            {
                concurrencySemaphore.Wait(_cancelToken);
                Task task = Task.Run(async () =>
                {
                    try
                    {
                        Directory.CreateDirectory(Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier));

                        Audio.EncodeAudio encodeAudio = new();
                        Video.VideoSplitter videoSplitter = new();
                        Video.VideoEncode videoEncoder = new();
                        Video.VideoMuxer videoMuxer = new();

                        await Task.Run(() => queueElement.GetFrameCount());

                        List<string> VideoChunks = new();

                        // Chunking
                        if (QueueParallel)
                        {
                            VideoChunks.Add(queueElement.VideoDB.InputPath);
                        }
                        else
                        {
                            await Task.Run(() => videoSplitter.Split(queueElement, _cancelToken), _cancelToken);

                            if (queueElement.ChunkingMethod == 0)
                            {
                                // Equal Chunking
                                string[] filePaths = Directory.GetFiles(Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier, "Chunks"), "*.mkv", SearchOption.TopDirectoryOnly);
                                foreach (string file in filePaths)
                                {
                                    VideoChunks.Add(file);
                                }
                            }
                            else
                            {
                                // Scene Detect
                                if (File.Exists(Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier, "splits.txt")))
                                {
                                    VideoChunks = File.ReadAllLines(Path.Combine(Global.Temp, "NEAV1E", queueElement.UniqueIdentifier, "splits.txt")).ToList();
                                }
                            }
                        }

                        if (VideoChunks.Count == 0)
                        {
                            queueElement.Status = "Error: No Video Chunk found";
                        }
                        else
                        {
                            // Audio Encoding
                            await Task.Run(() => encodeAudio.Encode(queueElement, _cancelToken), _cancelToken);

                            // Extract VFR Timestamps
                            await Task.Run(() => queueElement.GetVFRTimeStamps(), _cancelToken);

                            // Starts "a timer" for eta / fps calculation
                            System.Timers.Timer aTimer = new();
                            aTimer.Elapsed += (sender, e) => { UpdateProgressBar(sender, e, queueElement); };
                            aTimer.Interval = 1000;
                            aTimer.Start();

                            // Video Encoding
                            await Task.Run(() => videoEncoder.Encode(WorkerCountElement, VideoChunks, queueElement, _cancelToken, QueueParallel), _cancelToken);

                            aTimer.Stop();

                            await Task.Run(() => videoMuxer.Concat(queueElement), _cancelToken);

                            await Task.Run(() => DeleteTempFiles(queueElement), _cancelToken);
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
            LabelStartPauseButton.Content = "Start";

            Shutdown();
        }

        private static void UpdateProgressBar(object sender, EventArgs e, Queue.QueueElement queueElement)
        {
            long encodedFrames = 0;

            foreach (Queue.ChunkProgress _progress in queueElement.ChunkProgress)
            {
                try
                {
                    encodedFrames += _progress.Progress;
                }
                catch { }
            }

            queueElement.Progress = Convert.ToDouble(encodedFrames);
            queueElement.Status = "Encoded: " + ((decimal)encodedFrames / queueElement.FrameCount).ToString("0.00%");
        }

        #endregion

    }
}
