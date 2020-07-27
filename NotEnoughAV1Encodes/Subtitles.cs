﻿using System.IO;

namespace NotEnoughAV1Encodes
{
    class Subtitles
    {
        public static void EncSubtitles()
        {
            if (MainWindow.subtitleHardcoding == false)
            {
                if (!Directory.Exists(Path.Combine(MainWindow.tempPath, "Subtitles")))
                    Directory.CreateDirectory(Path.Combine(MainWindow.tempPath, "Subtitles"));

                if (MainWindow.subtitleCopy)
                {
                    string subtitleCommand = "/C ffmpeg.exe -i " + '\u0022' + MainWindow.videoInput + '\u0022' + " -vn -an -dn -c copy " + '\u0022' + MainWindow.tempPath + "\\Subtitles\\subtitle.mkv" + '\u0022';
                    SmallFunctions.Logging("EncSubtitles() Command: " + subtitleCommand);
                    SmallFunctions.ExecuteFfmpegTask(subtitleCommand);
                }
                if (MainWindow.subtitleCustom)
                {
                    string subtitleMapping = "", subtitleInput = "";
                    int subtitleAmount = 0;

                    foreach (var items in MainWindow.SubtitleChunks)
                    {
                        subtitleInput += " -i " + '\u0022' + items + '\u0022';
                        subtitleMapping += " -map " + subtitleAmount;
                        subtitleAmount += 1;
                    }

                    string subtitleCommand = "/C ffmpeg.exe" + subtitleInput + " -vn -an -dn -c copy " + subtitleMapping + " " + '\u0022' + MainWindow.tempPath + "\\Subtitles\\subtitle.mkv" + '\u0022';
                    SmallFunctions.Logging("EncSubtitles() Command: " + subtitleCommand);
                    SmallFunctions.ExecuteFfmpegTask(subtitleCommand);
                }
            }
        }
    }
}
