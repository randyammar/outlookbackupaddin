﻿using BackupAddInCommon;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace BackupExecutor
{
    /// <summary>
    ///  Utility class for backing up outlook files
    /// </summary>
    public class BackupTool
    {
        private static System.Windows.Forms.ProgressBar pbCopyProgress;
        private static System.Windows.Forms.ProgressBar pbTotalCopyProgress;
        private static System.Windows.Forms.Label lblFilename;
        private static System.Windows.Forms.Label lblMegaBytesPerSecond;

        /// <summary>
        /// Flag, whether user can close main window
        /// </summary>
        public static bool CanExit = false;

        /// <summary>
        /// Log-delegate for sending error information
        /// </summary>
        public delegate void Logger(string message);

        private const String OUTLOOK_PROC = "OUTLOOK";

        private static long TotalBytesToCopy; 
        private static long TotalBytesCopied;
        private static long StartTimeCopy;

        /// <summary>
        /// Set a label to report currently copied file
        /// </summary>
        public static void setFileLabel(System.Windows.Forms.Label lbl)
        {
            lblFilename = lbl;
        }

        /// <summary>
        /// Set a label to report transfer speed
        /// </summary>
        public static void setMegaByesPerSecondLabel(System.Windows.Forms.Label lbl)
        {
            lblMegaBytesPerSecond = lbl;
        }

        /// <summary>
        /// Set a progress bar to report copy progress
        /// </summary>
        public static void setProgressBar(System.Windows.Forms.ProgressBar pb)
        {
            pbCopyProgress = pb;
        }

        /// <summary>
        /// Set a progress bar to report status of whole copy progress
        /// </summary>
        public static void setTotalProgressBar(System.Windows.Forms.ProgressBar pb)
        {
            pbTotalCopyProgress = pb;
        }
        
  

        /// <summary>
        /// Waits, till outlook ends and then starts the backup process
        /// </summary>
        /// <param name="config">Stored configuration from outlook plugin</param>
        /// <param name="log">logging delegate to send error information</param>
        /// <returns>number of occured errors</returns>
        public static int tryBackup(BackupSettings config, Logger log)
        {
            int iError = 0;

            log("Starting backup...please wait...");

            if (config.Items.Count > 0)
            {
                if (WaitForProcessEnd(OUTLOOK_PROC, log))
                {
                    iError += doBackup(config, log);
                    if (!String.IsNullOrEmpty(config.PostBackupCmd))
                        iError += RunPostCmd(config.PostBackupCmd, log);
                }
                else
                {
                    iError++;
                    log("Error waiting for " + OUTLOOK_PROC);
                }
            }

            return iError;
        }

        /// <summary>
        /// Runs a program and waits for finish (after backup)
        /// </summary>
        /// <param name="cmd">Command to execute</param>
        /// <param name="log">logging delegate to send error information</param>
        /// <returns>number of occured errors</returns>
        private static int RunPostCmd(string cmd, Logger log)
        {
            log("Starting post-backup cmd: " + cmd);

            int iError = 0;
            Process p = new Process();
            p.StartInfo.WorkingDirectory = "";
            p.StartInfo.FileName = cmd;
            p.StartInfo.Arguments = "";

            try
            {
                p.Start();
                p.WaitForExit();
                return p.ExitCode;
            }
            catch (Exception e)
            {
                iError++;
                log("Error executing " + cmd + ": " + e.Message);
            }

            return iError;
        }

        /// <summary>
        /// Tries to backup the configures files from outlook
        /// </summary>
        /// <param name="config">Stored configuration from outlook plugin</param>
        /// <param name="log">logging delegate to send error information</param>
        /// <returns>number of occured errors</returns>
        private static int doBackup(BackupSettings config, Logger log)
        {
            int iError = 0, iSuccess = 0;
            String sPath = config.DestinationPath;

            if (!sPath.EndsWith(Path.DirectorySeparatorChar.ToString()))
                sPath += Path.DirectorySeparatorChar;

            String sDst;
            int iCounter = 0;

            //Gather all file sizes
            TotalBytesToCopy = 0;
            long[] FileSizes = new long[config.Items.Count];
            foreach (String item in config.Items)
            {
                FileSizes[iCounter] = (new System.IO.FileInfo(item)).Length;
                TotalBytesToCopy += FileSizes[iCounter];
                iCounter++;
            }

            //Copy files
            TotalBytesCopied = 0;
            iCounter = 0;
            foreach (String item in config.Items)
            {
                iCounter++;
                try
                {
                    if (lblFilename != null)
                        lblFilename.Invoke(new Action(() => lblFilename.Text = "File " + iCounter + "/" + config.Items.Count + ": " + item));

                    if (config.UseCompression)
                        sDst = sPath + config.BackupPrefix + Path.GetFileName(item) + ".gz" + config.BackupSuffix;
                    else
                        sDst = sPath + config.BackupPrefix + Path.GetFileName(item) + config.BackupSuffix;

                    if (item.Equals(sDst))
                    {
                        log("Can't copy file on it's own, skipping: " + item);
                        iError++;
                    }
                    else
                    {
                        //src and dest are different, lets backp
                        log("copy " + item + " to " + config.DestinationPath);
                        WaitForFile(item, log, config.WaitTimeFileLock);

                        if (config.UseCompression)
                            CopyAndCompressFileForBackup(sDst, item);
                        else
                            CopyFileForBackup(config, sDst, item);

                        iSuccess++;
                    }

                    TotalBytesCopied += FileSizes[iCounter - 1];
                }
                catch (Exception e)
                {
                    iError++;
                    log(e.Message);
                }
            }

            if (iSuccess > 0)
            {
                config.LastRun = DateTime.Now;
                BackupSettingsDao.saveSettings(config);
            }

            return iError;
        }

        private static void CopyAndCompressFileForBackup(String sDst, String item)
        {
            FileInfo fi = new FileInfo(item);

            // Get the stream of the source file.
            using (FileStream inFile = fi.OpenRead())
            {
                // Create the compressed file.
                using (FileStream outFile = File.Create(sDst))
                {
                    GZipStream Compress = new GZipStream(outFile, CompressionMode.Compress);
                    byte[] buffer = new byte[16 * 1024];
                    int read;
                    while ((read = inFile.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        Compress.Write(buffer, 0, read);
                    };

                    Compress.Close();
                }
            }
        }

        private static void CopyFileForBackup(BackupSettings config, String sDst, String item)
        {
            SafeNativeMethods.CopyFileFlags dwCopyFlags = 0;
            if (config.IgnoreEncryption)
                dwCopyFlags = SafeNativeMethods.CopyFileFlags.COPY_FILE_ALLOW_DECRYPTED_DESTINATION;

            bool pbCancel = false;
            SafeNativeMethods.CopyProgressRoutine cb = new SafeNativeMethods.CopyProgressRoutine(callback);

            StartTimeCopy = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;

            //CopyFileEx
            //https://msdn.microsoft.com/en-us/library/windows/desktop/aa363852%28v=vs.85%29.aspx
            SafeNativeMethods.CopyFileEx(item, sDst, cb, IntPtr.Zero, ref pbCancel, dwCopyFlags);
        }

        private static SafeNativeMethods.CopyProgressResult callback(long TotalFileSize,
            long TotalFileBytesTransferred, long StreamSize,
            long StreamBytesTransferred, uint dwStreamNumber,
            SafeNativeMethods.CopyProgressCallbackReason dwCallbackReason,
            IntPtr hSourceFile, IntPtr hDestinationFile, IntPtr lpData)
        {
            int i = (int)(TotalFileBytesTransferred * 100 / StreamSize);
            if (pbCopyProgress != null)
                pbCopyProgress.Invoke(new Action(() => pbCopyProgress.Value = i));

            int iTotal = (int)((TotalBytesCopied + TotalFileBytesTransferred) * 100 / TotalBytesToCopy);
            if (pbTotalCopyProgress != null)
                pbTotalCopyProgress.Invoke(new Action(() => pbTotalCopyProgress.Value = iTotal));

            double TimeElapsedSec = (DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond - StartTimeCopy) / 1000.0;
            if (TimeElapsedSec > 0.1 && lblMegaBytesPerSecond != null && TotalFileBytesTransferred > 0)
            {
                int iMbPerSec = (int)(TotalFileBytesTransferred / 1024 / 1024 / TimeElapsedSec);
                lblMegaBytesPerSecond.Invoke(new Action(() => lblMegaBytesPerSecond.Text = iMbPerSec + " MiB/s"));
            }

            return SafeNativeMethods.CopyProgressResult.PROGRESS_CONTINUE;
        }

        /// <summary>
        /// Wait max. 5 seconds for releasing locks on the file
        /// </summary>
        /// <param name="item">filename including path</param>
        /// <param name="log">logging delegate to send error information</param>
        /// <param name="waittime">wait time if file is locked</param>
        /// <returns></returns>
        private static bool WaitForFile(string item, Logger log, int waittime = 500)
        {
            int i = 0;
            FileInfo fi = new FileInfo(item);
            while (IsFileLocked(fi, log) && i < 10)
            {
                Thread.Sleep(waittime);
                i++;
            }

            return (i < 10);
        }

 
        /// <summary>
        /// Waits, till the process is not running any more
        /// </summary>
        /// <param name="name">process name (!) within task manager</param>
        /// <param name="log">logging delegate to send error information</param>
        /// <returns>returns true if the process is not running any more</returns>
        public static bool WaitForProcessEnd(string name, Logger log)
        {
            int i = 0;
            while (i < 10 && IsProcessOpen(name, log))
            {
                log("Waiting for " + name + "...");
                Application.DoEvents();
                System.Threading.Thread.Sleep(1000);
                i++;
            }

            return !IsProcessOpen(name, log);
        }

        /// <summary>
        /// Checks whether a certain process is running
        /// </summary>
        /// <param name="name">process name as seen witin the task  manager</param>
        /// <param name="log">logging delegate to send error information</param>
        /// <returns>true, if process is still running</returns>
        public static bool IsProcessOpen(string name, Logger log)
        {
            foreach (Process clsProcess in Process.GetProcesses())
            {
                try
                {
                    //txtLog.Text += clsProcess.ProcessName + Environment.NewLine;// MainModule.ModuleName ;
                    if (clsProcess.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        log("Found outlook with PID " + clsProcess.Id);
                        return true;
                    }
                }
                catch (Exception)
                {
                    //some processes are dont like to be inspected... just go on...
                }
            }
            return false;
        }

        /// <summary>
        /// Check whether a file is locked 
        /// </summary>
        /// <param name="file">file to be checked</param>
        /// <param name="log">logging delegate to send error information</param>
        /// <returns></returns>
        protected static bool IsFileLocked(FileInfo file, Logger log)
        {
            if (!file.Exists)
                return false;

            FileStream stream = null;

            try
            {
                stream = file.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {

                //the file is unavailable because it is:
                //still being written to
                //or being processed by another thread
                //or does not exist 
                log("File is locked: " + file);
                return true;
            }
            finally
            {
                if (stream != null)
                {
                    //stream.Close();
                    //get sure to close all handles (also own one)
                    stream.Dispose();
                    stream = null;
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }

            //file is not locked
            return false;
        }

    }
}
