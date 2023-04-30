﻿using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xabe.FFmpeg;

namespace Rotoscope
{
    /// <summary>
    /// Manages loading, saving, and accessing frames from a movie file.
    /// </summary>
    public class Movie
    {
        #region Data locations
        private static int id = 0;
        IAudioStream audio = null;
        private string imageSaveName = "frameImage";
        private string imageSavePath = "tempImages\\";
        private string audioSavePath = "tempAudio\\";
        Sound savedSound;
        private string tempAudioSaveFile;
        IVideoStream video;

        #endregion

        #region Properties

        /// <summary>
        /// Gets/set the frame rate
        /// </summary>
        public double FrameRate { get => frameRate; set => frameRate = value; }

        /// <summary>
        /// True if there is any frames left
        /// </summary>
        public bool HasMoreVideo { get => position / frameRate <= duration.TotalSeconds; }

        /// <summary>
        /// Height of the movie in pixels
        /// </summary>
        public int Height { get => height; set => height = value; }

        /// <summary>
        /// Is the movie open
        /// </summary>
        public bool IsOpen { get => isOpen; set => isOpen = value; }

        /// <summary>
        /// Current Frame index
        /// </summary>
        public int Position { get => position; }

        /// <summary>
        /// Total number of frames it the video
        /// </summary>
        public int TotalFrames { get => (int)(frameRate * duration.TotalSeconds); }

        /// <summary>
        /// Width of the movie in pixels
        /// </summary>
        public int Width { get => width; set => width = value; }

        private LinkedList<Bitmap> cache = new LinkedList<Bitmap>();
        private TimeSpan duration;
        private double frameRate = 0;
        private int height = 0;
        private bool isOpen = false;
        private Bitmap lastLoadedBitmap;
        private int position = 0;
        private int width = 0;
        private int pullAmount = 3;

        #endregion

        /// <summary>
        /// Constructor for an empty movie maker
        /// </summary>
        public Movie()
        {
            tempAudioSaveFile = audioSavePath + "tempAudio" + id + ".wav";
            id++;

            Close();

            if (!Directory.Exists(imageSavePath))
            {
                Directory.CreateDirectory(imageSavePath);
            }

            if (!Directory.Exists(audioSavePath))
            {
                Directory.CreateDirectory(audioSavePath);
            }
        }

       
        /// <summary>
        /// Destructor that cleans up image files created during processing
        /// </summary>
        ~Movie()
        {
            Close();
        }


        #region file save, open

        /// <summary>
        /// Release movie's data sources
        /// </summary>
        public void Close()
        {
            audio = null;
            video = null;
            isOpen = false;

            if (savedSound != null)
            {
                savedSound.Close();
                savedSound = null;
            }

            //possibly created files, delete them
            if (Directory.Exists(imageSavePath)){
                foreach (string file in Directory.GetFiles(imageSavePath))
                {
                    MainForm.VolitilePermissionDelete(file);
                }
            }

            if (Directory.Exists(audioSavePath))
            {
                foreach (string file in Directory.GetFiles(audioSavePath))
                {
                    MainForm.VolitilePermissionDelete(file);
                }
            }

        }

        /// <summary>
        /// Open a given movie file. Runs asyncrounously.
        /// </summary>
        /// <param name="fileName">path to file</param>
        /// <returns>Generic task</returns>
        public void Open(string fileName)
        {
            //get streams
            Task<IMediaInfo> task = Task.Run(() => FFmpeg.GetMediaInfo(fileName));
            try
            {
                IMediaInfo mediaInfo = task.Result;

                audio = mediaInfo.AudioStreams.FirstOrDefault();
                video = mediaInfo.VideoStreams.First().SetCodec(VideoCodec.png);

                //get common parameters
                duration = mediaInfo.Duration;
                frameRate = video.Framerate;
                height = video.Height;
                width = video.Width;
                position = 0;
                isOpen = true;

                LoadNextFrameImage();
            }
            catch (Exception)
            {
                MessageBox.Show("Could not open. " +
                    "This is usually due to a missign file or FFmpeg is not installed."
                   , "Could not Open");
            }
        }

        /// <summary>
        /// Save movie to a given location. Works with mp4/avi/wmv (in theory)
        /// </summary>
        /// <param name="fileName">path to file</param>
        public void Save(string fileName)
        {
            if (File.Exists(fileName))
            {
                File.Delete(fileName);
            }

            //codex
            IStream videoStream = video.SetCodec(VideoCodec.h264);
            IStream audioStream = audio.SetCodec(AudioCodec.aac);

            //convert
            FFmpeg.Conversions.New()
                     .AddStream(audioStream, videoStream)
                     .SetOutput(fileName)
                     .Start();
        }
        #endregion


        #region Pull info

        /// <summary>
        /// Creates a Sound object from the audio stream.
        /// </summary>
        /// <returns>A Sound object loaded from the movie's audio stream</returns>
        public Sound GetAudio()
        {
            string tempLoc = "temp\\trial.wav";
            //if already done, return the sound
            if (savedSound!= null)
            {
                return savedSound;
            }

            try
            {
                //convert from stream to file
                IConversion convert =
                    FFmpeg.Conversions.New()
                    .AddStream(audio)
                    .SetOverwriteOutput(true)
                    .AddParameter("-ss 00:00:00.000") //because the encode pads silence otherwise...
                    .SetOutput(tempLoc);

                Task<IConversionResult> task = Task.Run(() => convert.Start());
                task.Wait();

                shortToIEEEConvert(tempLoc, tempAudioSaveFile);
                MainForm.VolitilePermissionDelete(tempLoc); //clean up

                //open the sound file, and load into raw data
                savedSound = new SoundStream(tempAudioSaveFile);
                return savedSound;
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "Audio Pull Error");
            }
            return null;
        }

        /// <summary>
        /// Most movies pull audio with 16 bit short. This converts it
        /// </summary>
        /// <param name="inFile"></param>
        /// <param name="outFile"></param>
        private void shortToIEEEConvert(string inFile, string outFile)
        {
            //open file
            Stream stream = new FileStream(inFile, FileMode.Open);
            WaveFileReader readerStream = new WaveFileReader(stream);

            //save format
            WaveFormat format = readerStream.WaveFormat;

            //already a IEEE encoding, just copy
            if (format.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                WaveFormat f = WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);
                using (WaveFileWriter writer = new WaveFileWriter(outFile, f))
                {
                    readerStream.CopyTo(writer);
                }
            }
            else
            {
                //otherwise convert
                WaveFormat f = WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);
                using (WaveFileWriter writer = new WaveFileWriter(outFile, f))
                {
                    //read byte, convert to short, then to float
                    byte[] frame = new byte[format.Channels * 2];
                    float[] sample = new float[format.Channels];
                    short[] sdata = new short[format.Channels];
                    int count = readerStream.Read(frame, 0, format.Channels * 2);

                    //while there is data left, convert from 16bit to -1 - 1 float
                    while (count > 0)
                    {
                        Buffer.BlockCopy(frame, 0, sdata, 0, frame.Length);
                        for (int i = 0; i < sdata.Length; i++)
                        {
                            sample[i] = (float)(sdata[i]) / short.MaxValue;
                        }
                        writer.WriteSamples(sample, 0, sample.Length);
                        count = readerStream.Read(frame, 0, format.Channels * 2);

                    }
                }
            }

            stream.Close();
            stream = null;
            readerStream.Close();
            readerStream = null;
        }

        /// <summary>
        /// Reads the next frame (image) in the video stream based on the current positition into the stream.
        /// 
        /// Loading is done asyncrounously when possible.
        /// </summary>
        /// <returns>A Bitmap of the frame</returns>
        public Bitmap LoadNextFrameImage()
        {
          
            //start up loading more
            if (cache.Count() == 0)
            {
                Task task = Task.Run(() => GetNextSet());
                task.Wait();

                //no frames left!
                if (cache.Count() == 0)
                {
                    lastLoadedBitmap = null;
                }
            }

            if (cache.Count() >= 1)
            {
                Bitmap newImage = null;
                using (var image = new Bitmap(cache.First()))
                {
                    newImage = new Bitmap(image);
                }
                lastLoadedBitmap = newImage;
               
                cache.RemoveFirst();
               
            }
            position++;
            return lastLoadedBitmap;
        }

        /// <summary>
        /// Get's the next set of frame to load into the cache. 
        /// </summary>
        /// <returns>a generic task</returns>
        private async Task GetNextSet()
        {
            try
            {
                string path = imageSavePath + imageSaveName;
                Func<string, string> outputFileNameBuilder = (number) =>
                {
                    return path + "_" + number + ".png";
                };


                //determine current location in the stream
                double time = position / frameRate;
                double fractional = time % 1;
                int sec = (int)time;
                int hours = sec / 3600;
                int minutes = (sec - hours * 3600) / 60;
                int seconds = sec - hours * 3600 - minutes * 60;
                int millisecond = (int)(fractional * 1000);

                //split time
                string fmt = "00";
                string splitStr = "-ss " + hours.ToString(fmt) + ":" +
                    minutes.ToString(fmt) + ":" + seconds.ToString(fmt) + 
                    "." + millisecond.ToString(fmt) + " -t 00:00:" + pullAmount.ToString(fmt);

                IConversion framePullConversion = FFmpeg.Conversions.New()
                    .AddStream(video)
                    .AddParameter(splitStr)
                    .ExtractEveryNthFrame(1, outputFileNameBuilder);

                await framePullConversion.Start();

                //load pulled frames into memory
                foreach (string file in Directory.GetFiles(imageSavePath))
                {
                    Bitmap newImage = null;
                    using (var image = new Bitmap(file)) //disconnect from file system
                    {
                        newImage = new Bitmap(image);
                    }
                    cache.AddLast(newImage);

                    //delete file
                    MainForm.VolitilePermissionDelete(file);
                }

            }
            catch(Exception e)
            {
                MessageBox.Show(e.Message, "Frame Pull Error");
            }
        }
        #endregion
    }
}
