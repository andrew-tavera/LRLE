﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using LRLE;
using Microsoft.Extensions.Configuration;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Runtime;

namespace LREParser
{
    class MainClass
    {
        static string outDir = "out";
        static bool roundtrip = false;
        static bool saveMips = false;
        static int maxMips = 12;
        static bool fast = false;

        static string[] extensions = new[] { ".lrle", ".png" };
        public static void Main(string[] args)
        {
            string inputPath = GetInputPath(args);
            if (!Directory.Exists(inputPath) && !File.Exists(inputPath))
            {
                Console.WriteLine("Please provide an input path (image file or folder with images in it)");
                return;
            }
            string outputFolder = CreateOutputFolder(inputPath);
            var files = GetFilesFromInputPath(inputPath);
            if (!files.Any())
            {
                Console.WriteLine($"No image files found in {inputPath}");
                return;
            }

            ParseCommandLineOptions(args);
            GC.Collect(2);
            GCSettings.LatencyMode = GCLatencyMode.LowLatency;
            using (Timer("All files"))
            {
                foreach (var lrle in files)
                {
                    using (Timer(Path.GetFileName(lrle)))
                    {
                        ProcessLRLE(lrle, Path.Combine(outputFolder, Path.GetFileName(lrle)), roundtrip);
                    }
                }
            }
        }

        static string FormatColor(int c) => FormatColor((uint)c);
        static string FormatColor(uint c) => $"#{c & 0x00FFFFFF:X6}{(c >> 24):X2}";

        static byte[] GetPixelData(Bitmap bmp)
        {
            var bits = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var buffer = new byte[bmp.Width * bmp.Height << 2];
            Marshal.Copy(bits.Scan0, buffer, 0, buffer.Length);
            bmp.UnlockBits(bits);
            return buffer;
        }
        private static void ProcessLRLE(string inputFile, string outputFile, bool convertBack)
        {
            var inputCopyDest = Path.Combine(Path.GetDirectoryName(outputFile), Path.GetFileName(inputFile));
            if (inputCopyDest != inputFile) File.Copy(inputFile, inputCopyDest);
            if (Path.GetExtension(inputFile) == ".png")
            {
                using (Timer($"Convert input {Path.GetFileName(inputFile)} to LRLE"))
                {
                    var lrle = outputFile += ".lrle";
                    ConvertPNGToLRLE(inputFile, lrle);
                    inputFile = lrle;
                }
            }

            using (var fs = File.OpenRead(inputFile))
            {
                var lrleReader = LRLEUtility.GetReader(fs);
                var lrleWriter = LRLEUtility.GetWriter();
                Bitmap[] bitmaps = new Bitmap[lrleReader.MipCount];
                using (Timer("All mipmaps decoded"))
                {

                    foreach (var mip in lrleReader.Read().Take(maxMips))
                    {
                        Bitmap bmp;
                        using (Timer($"Decoding mip {mip.Index}"))
                        {
                            bmp = ExtractMipMapData(mip);
                        }
                        bitmaps[mip.Index] = bmp;
                    }
                }
                using (Timer("All mip maps saved"))
                {
                    for (int i = 0; i < bitmaps.Length; i++)
                    {
                        var bmp = bitmaps[i];
                        if (saveMips)
                        {
                            using (Timer($"Saving mip {i}"))
                            {
                                bmp.Save(outputFile + $".mip{i}.png");
                            }
                        }
                        else if (i == 0)
                        {
                            using (Timer($"Saving first mip"))
                            {
                                bmp.Save(outputFile + $".png");
                            }
                        }
                    }
                }
                if (convertBack)
                {
                    using (Timer($"All mip data extracted for re-encode"))
                    {
                        for (int i = 0; i < lrleReader.MipCount; i++)
                        {
                            var bmp = bitmaps[i];
                            using (Timer($"Extracting pixels from decoded mip {i}"))
                            {
                                lrleWriter.AddMip(bmp.Width, bmp.Height, GetPixelData(bmp));
                            }
                        }

                    }
                    var outFile = outputFile + $".out";
                    //Re-encode the pixel runs as a new lrle file
                    ReEncode(lrleWriter, outFile);
                    //Attempt to re-parse the newly written lrle file
                    ReDecode(outFile);
                }
            }
        }

        private static void ConvertPNGToLRLE(string inputFile, string lrle)
        {
            using (var fs = File.Create(lrle))
            {
                var bmp = (Bitmap)Image.FromFile(inputFile);
                var encoder = LRLEUtility.GetWriter();
                int w = bmp.Width;
                int h = bmp.Height;
                List<Bitmap> bitmaps = new List<Bitmap>();
                using (Timer("All mips resized"))
                {
                    int mip = 0;
                    Bitmap mipBmp = bmp;
                    do
                    {
                        int mipWidth = w >> mip;
                        int mipHeight = h >> mip;
                        using (Timer($"Resizing mip {mip}"))
                        {
                            if (w != mipWidth || h != mipHeight)
                            {
                                mipBmp = new Bitmap(w >> mip, h >> mip);
                                mipBmp.SetResolution(bmp.HorizontalResolution, bmp.VerticalResolution);
                                using (var g = Graphics.FromImage(mipBmp))
                                {
                                    if (!fast)
                                    {
                                        g.CompositingMode = CompositingMode.SourceCopy;
                                        g.CompositingQuality = CompositingQuality.HighQuality;
                                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                                        g.SmoothingMode = SmoothingMode.HighQuality;
                                    }
                                    g.DrawImage(bmp, new Rectangle(0, 0, mipBmp.Width, mipBmp.Height));
                                }
                            }
                        }
                        bitmaps.Add(mipBmp);
                        mip++;
                    }
                    while (mipBmp.Width >= 8 && mipBmp.Height >= 8);
                }
                using (Timer($"Extracted all pixel runs"))
                { 
                    int mip = 0;
                    foreach (var b in bitmaps)
                    {
                        using (Timer($"Extracting mip {mip++} pixel runs"))
                        {
                            var bits = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                            byte[] bytes = new byte[b.Width * b.Height << 2];
                            Marshal.Copy(bits.Scan0, bytes, 0, bytes.Length);
                            b.UnlockBits(bits);
                            encoder.AddMip(b.Width, b.Height, bytes);
                        }
                    }
                }
                using (Timer("Writing LRLE"))
                    encoder.Write(fs);
            }
        }

        private static void ReEncode(LRLEUtility.Writer lrleWriter, string outFile)
        {
            using (Timer($"Write {outFile} to file"))
            {
                using (var rt = File.Create(outFile))
                {
                    lrleWriter.Write(rt);
                }
            }
        }
        private static void ReDecode(string outFile)
        {
            try
            {
                ProcessLRLE(outFile, outFile, false);
                Console.WriteLine($"Re-parsed {outFile}!");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Failed to re-parse {outFile}\n{e}");
            }
        }

        private static Bitmap ExtractMipMapData(LRLEUtility.Reader.Mip mip)
        {
            var bmp = new Bitmap(mip.Width, mip.Height);
            if (saveMips || mip.Index == 0)
            {
                var bits = bmp.LockBits(new Rectangle(0, 0, mip.Width, mip.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                mip.Read(bits.Scan0);
                bmp.UnlockBits(bits);
            }
            return bmp;

        }
        class ConsoleStopWatch : IDisposable
        {
            private readonly string subject;
            private readonly Stopwatch stopwatch;

            public ConsoleStopWatch(string subject)
            {
                this.subject = subject;
                this.stopwatch = new Stopwatch();
                this.stopwatch.Start();
            }

            public void Dispose()
            {
                this.Stop();
            }

            private void Stop()
            {
                if (this.stopwatch.IsRunning)
                {
                    this.stopwatch.Stop();
                }
                Console.WriteLine($"{subject} finished in {(stopwatch.ElapsedMilliseconds / 1000.0)} s");
            }
        }

        private static void ParseCommandLineOptions(string[] args)
        {
            var conf = new ConfigurationBuilder()
                .AddCommandLine(args)
                .Build();
            outDir = conf[nameof(outDir)] ?? "out";
            maxMips = int.Parse(conf[nameof(maxMips)] ?? "10");
            roundtrip = (conf[nameof(roundtrip)] ?? "false").ToLower() == "true";
            saveMips = (conf[nameof(saveMips)] ?? "false").ToLower() == "true";
            fast = (conf[nameof(fast)] ?? "false").ToLower() == "true";
        }

        private static string GetInputPath(string[] args)
        {
            return args.Length == 0 ?
                            Path.GetDirectoryName(typeof(MainClass).Assembly.Location) :
                            args[0];
        }

        private static List<string> GetFilesFromInputPath(string f)
        {
            var files = new List<string>();
            if (Directory.Exists(f))
                files.AddRange(extensions.SelectMany(x => Directory.EnumerateFiles(f, $"*{x}")));
            else if (File.Exists(f) && extensions.Contains(Path.GetExtension(f))) files.Add(f);
            return files;
        }


        private static string CreateOutputFolder(string f)
        {
            string o;
            if (File.Exists(f))
            {
                o = Path.Combine(Path.GetDirectoryName(f), outDir);
            }
            else
            {
                o = Path.Combine(f, outDir);
            }
            if (Directory.Exists(o)) Directory.Delete(o, true);
            Directory.CreateDirectory(o);
            return o;
        }
        private static IDisposable Timer(string subject)
        {
            return new ConsoleStopWatch(subject);
        }
    }
}


