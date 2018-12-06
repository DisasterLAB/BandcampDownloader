﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using ImageResizer;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace BandcampDownloader {

    public partial class WindowMain: Window {

        #region Fields

        /// <summary>
        /// True if there are active downloads; false otherwise.
        /// </summary>
        private Boolean activeDownloads = false;
        /// <summary>
        /// Used to update the downloaded bytes / files count on the UI.
        /// </summary>
        private ConcurrentQueue<DownloadProgress> downloadProgresses;
        /// <summary>
        /// The files to download, or being downloaded, or downloaded. Used to compute the current received bytes and the total bytes to download.
        /// </summary>
        private List<TrackFile> filesDownload;
        /// <summary>
        /// Used to compute and display the download speed.
        /// </summary>
        private DateTime lastDownloadSpeedUpdate;
        /// <summary>
        /// Used to compute and display the download speed.
        /// </summary>
        private long lastTotalReceivedBytes = 0;
        /// <summary>
        /// Used when user clicks on 'Cancel' to abort all current downloads.
        /// </summary>
        private List<WebClient> pendingDownloads;
        /// <summary>
        /// Used when user clicks on 'Cancel' to manage the cancelation (UI...).
        /// </summary>
        private Boolean userCancelled;

        #endregion

        #region Constructor

        public WindowMain() {
            // Save DataContext for bindings (must be called before initializing UI)
            DataContext = App.UserSettings;

            InitializeLogger();
            InitializeComponent();

            // Increase the maximum of concurrent connections to be able to download more than 2 (which is the default value) files at the same time
            ServicePointManager.DefaultConnectionLimit = 50;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            // Hints
            textBoxUrls.Text = Constants.UrlsHint;
            textBoxUrls.Foreground = new SolidColorBrush(Colors.DarkGray);
            // Check for updates
            if (App.UserSettings.CheckForUpdates) {
                Task.Factory.StartNew(() => { CheckForUpdates(); });
            }
#if DEBUG
            textBoxUrls.Text = ""
                + "https://goataholicskjald.bandcamp.com/album/dogma" /* #65 Downloaded size ≠ predicted */ + Environment.NewLine
                //+ "https://mstrvlk.bandcamp.com/album/-" /* #64 Album with big cover */ + Environment.NewLine
                //+ "https://mstrvlk.bandcamp.com/track/-" /* #64 Track with big cover */ + Environment.NewLine
                //+ "https://weneverlearnedtolive.bandcamp.com/album/silently-i-threw-them-skyward" /* #42 Album with lyrics */ + Environment.NewLine
                //+ "https://weneverlearnedtolive.bandcamp.com/track/shadows-in-hibernation-2" /* #42 Track with lyrics */ + Environment.NewLine
                //+ "https://goataholicskjald.bandcamp.com/track/europa" + Environment.NewLine
                //+ "https://goataholicskjald.bandcamp.com/track/epilogue" + Environment.NewLine
                //+ "https://afterdarkrecordings.bandcamp.com/album/adr-unreleased-tracks" /* #69 Album without cover */ + Environment.NewLine
                ;
#endif
        }

        #endregion

        #region Methods

        /// <summary>
        /// Displays a message if a new version is available.
        /// </summary>
        private void CheckForUpdates() {
            Version latestVersion = null;
            try {
                latestVersion = UpdatesHelper.GetLatestVersion();
            } catch (CouldNotCheckForUpdatesException) {
                Dispatcher.BeginInvoke(new Action(() => {
                    labelVersion.Content += " - Could not check for updates";
                }));
                return;
            }

            Version currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
            if (currentVersion.CompareTo(latestVersion) < 0) {
                // The latest version is newer than the current one
                Dispatcher.BeginInvoke(new Action(() => {
                    labelVersion.Content += " - A new version is available";
                }));
            }
        }

        /// <summary>
        /// Downloads an album.
        /// </summary>
        /// <param name="album">The album to download.</param>
        /// <param name="downloadsFolder">The path where to save the album.</param>
        private async Task DownloadAlbumAsync(Album album, String downloadsFolder) {
            if (this.userCancelled) {
                // Abort
                return;
            }

            // Create directory to place track files
            try {
                Directory.CreateDirectory(downloadsFolder);
            } catch {
                Log("An error occured when creating the album folder. Make sure you have the rights to write files in the folder you chose", LogType.Error);
                return;
            }

            TagLib.Picture artwork = null;

            // Download artwork
            if ((App.UserSettings.SaveCoverArtInTags || App.UserSettings.SaveCoverArtInFolder) && album.HasArtwork) {
                artwork = await DownloadCoverArtAsync(album, downloadsFolder);
            }

            // Download & tag tracks
            //Task[] tasks = new Task[album.Tracks.Count];
            Boolean[] tracksDownloaded = new Boolean[album.Tracks.Count];
            //for (int i = 0; i < album.Tracks.Count; i++) {
            //    // Temporarily save the index or we will have a race condition exception when i hits its maximum value
            //    int currentIndex = i;
            //    tasks[currentIndex] = Task.Factory.StartNew(() => tracksDownloaded[currentIndex] = DownloadAndTagTrack(downloadsFolder, album, album.Tracks[currentIndex], artwork));
            //}

            Int32[] indexes = Enumerable.Range(0, album.Tracks.Count).ToArray();
            await Task.WhenAll(indexes.Select(async i => tracksDownloaded[i] = await DownloadAndTagTrackAsync(downloadsFolder, album, album.Tracks[i], artwork)));

            // Wait for all tracks to be downloaded before saying the album is downloaded
            //Task.WaitAll(tasks);

            if (!this.userCancelled) {
                // Tasks have not been aborted
                if (tracksDownloaded.All(x => x == true)) {
                    Log($"Successfully downloaded album \"{album.Title}\"", LogType.Success);
                } else {
                    Log($"Finished downloading album \"{album.Title}\". Some tracks were not downloaded", LogType.Success);
                }
            }
        }

        /// <summary>
        /// Downloads and tags a track. Returns true if the track has been correctly downloaded; false otherwise.
        /// </summary>
        /// <param name="albumDirectoryPath">The path where to save the tracks.</param>
        /// <param name="album">The album of the track to download.</param>
        /// <param name="track">The track to download.</param>
        /// <param name="artwork">The cover art.</param>
        private async Task<Boolean> DownloadAndTagTrackAsync(String albumDirectoryPath, Album album, Track track, TagLib.Picture artwork) {
            Log($"Downloading track \"{track.Title}\" from url: {track.Mp3Url}", LogType.VerboseInfo);

            // Set path to save the file
            String trackPath = albumDirectoryPath + "\\" + GetFileName(album, track);
            if (trackPath.Length > 256) {
                // Shorten the path (Windows doesn't support a path > 256 characters)
                trackPath = albumDirectoryPath + "\\" + GetFileName(album, track).Substring(0, 3) + Path.GetExtension(trackPath);
            }
            int tries = 0;
            Boolean trackDownloaded = false;

            if (File.Exists(trackPath)) {
                long length = new FileInfo(trackPath).Length;
                foreach (TrackFile trackFile in filesDownload) {
                    if (track.Mp3Url == trackFile.Url &&
                        trackFile.Size > length - (trackFile.Size * App.UserSettings.AllowedFileSizeDifference) &&
                        trackFile.Size < length + (trackFile.Size * App.UserSettings.AllowedFileSizeDifference)) {
                        Log($"Track already exists within allowed file size range: track \"{GetFileName(album, track)}\" from album \"{album.Title}\" - Skipping download!", LogType.IntermediateSuccess);
                        return false;
                    }
                }
            }

            do {
                //var doneEvent = new AutoResetEvent(false);

                using (var webClient = new WebClient()) {
                    if (webClient.Proxy != null) {
                        webClient.Proxy.Credentials = CredentialCache.DefaultNetworkCredentials;
                    }

                    // Update progress bar when downloading
                    webClient.DownloadProgressChanged += (s, e) => {
                        this.downloadProgresses.Enqueue(new DownloadProgress(track.Mp3Url, e.BytesReceived));
                        UpdateProgress();
                    };

                    // Warn & tag when downloaded
                    webClient.DownloadFileCompleted += async (s, e) => {
                        await WaitForCooldownAsync(tries);
                        tries++;

                        if (!e.Cancelled && e.Error == null) {
                            trackDownloaded = true;

                            if (App.UserSettings.ModifyTags) {
                                // Tag (ID3) the file when downloaded
                                TagLib.File tagFile = TagLib.File.Create(trackPath);
                                tagFile = TagHelper.UpdateArtist(tagFile, album.Artist, App.UserSettings.TagArtist);
                                tagFile = TagHelper.UpdateAlbumArtist(tagFile, album.Artist, App.UserSettings.TagAlbumArtist);
                                tagFile = TagHelper.UpdateAlbumTitle(tagFile, album.Title, App.UserSettings.TagAlbumTitle);
                                tagFile = TagHelper.UpdateAlbumYear(tagFile, (uint) album.ReleaseDate.Year, App.UserSettings.TagYear);
                                tagFile = TagHelper.UpdateTrackNumber(tagFile, (uint) track.Number, App.UserSettings.TagTrackNumber);
                                tagFile = TagHelper.UpdateTrackTitle(tagFile, track.Title, App.UserSettings.TagTrackTitle);
                                tagFile = TagHelper.UpdateTrackLyrics(tagFile, track.Lyrics, App.UserSettings.TagLyrics);
                                tagFile = TagHelper.UpdateComments(tagFile, App.UserSettings.TagComments);
                                tagFile.Save();
                            }

                            if (App.UserSettings.SaveCoverArtInTags && artwork != null) {
                                // Save cover in tags when downloaded
                                TagLib.File tagFile = TagLib.File.Create(trackPath);
                                tagFile.Tag.Pictures = new TagLib.IPicture[1] { artwork };
                                tagFile.Save();
                            }

                            // Note the file as downloaded
                            TrackFile currentFile = this.filesDownload.Where(f => f.Url == track.Mp3Url).First();
                            currentFile.Downloaded = true;
                            Log($"Downloaded track \"{GetFileName(album, track)}\" from album \"{album.Title}\"", LogType.IntermediateSuccess);
                        } else if (!e.Cancelled && e.Error != null) {
                            if (tries < App.UserSettings.DownloadMaxTries) {
                                Log($"Unable to download track \"{GetFileName(album, track)}\" from album \"{album.Title}\". Try {tries} of {App.UserSettings.DownloadMaxTries}", LogType.Warning);
                            } else {
                                Log($"Unable to download track \"{GetFileName(album, track)}\" from album \"{album.Title}\". Hit max retries of {App.UserSettings.DownloadMaxTries}", LogType.Error);
                            }
                        } // Else the download has been cancelled (by the user)

                        //doneEvent.Set();
                    };

                    //lock (this.pendingDownloads) {
                        if (this.userCancelled) {
                            // Abort
                            return false;
                        }
                        // Register current download
                        this.pendingDownloads.Add(webClient);
                        // Start download
                        await webClient.DownloadFileTaskAsync(new Uri(track.Mp3Url), trackPath);
                    //}
                    // Wait for download to be finished
                    //doneEvent.WaitOne();
                    lock (this.pendingDownloads) {
                        this.pendingDownloads.Remove(webClient);
                    }
                }
            } while (!trackDownloaded && tries < App.UserSettings.DownloadMaxTries);

            return trackDownloaded;
        }

        /// <summary>
        /// Downloads the cover art and returns the one to save in tags.
        /// </summary>
        /// <param name="album">The album to download.</param>
        /// <param name="downloadsFolder">The path where to save the cover art.</param>
        private async Task<TagLib.Picture> DownloadCoverArtAsync(Album album, String downloadsFolder) {
            // Compute paths where to save artwork
            String artworkTempPath = Path.GetTempPath() + "\\" + album.Title.ToAllowedFileName() + Path.GetExtension(album.ArtworkUrl);
            String artworkFolderPath = downloadsFolder + "\\" + album.Title.ToAllowedFileName() + Path.GetExtension(album.ArtworkUrl);
            if (artworkTempPath.Length > 256 || artworkFolderPath.Length > 256) {
                // Shorten the path (Windows doesn't support a path > 256 characters)
                // There may be only one path to shorten, but it's better to use the same file name in both places
                artworkTempPath = Path.GetTempPath() + "\\" + album.Title.ToAllowedFileName().Substring(0, 3) + Path.GetExtension(album.ArtworkUrl);
                artworkFolderPath = downloadsFolder + "\\" + album.Title.ToAllowedFileName().Substring(0, 3) + Path.GetExtension(album.ArtworkUrl);
            }

            TagLib.Picture artworkInTags = null;

            int tries = 0;
            Boolean artworkDownloaded = false;

            do {
                //var doneEvent = new AutoResetEvent(false);

                using (var webClient = new WebClient()) {
                    if (webClient.Proxy != null) {
                        webClient.Proxy.Credentials = System.Net.CredentialCache.DefaultNetworkCredentials;
                    }

                    // Update progress bar when downloading
                    webClient.DownloadProgressChanged += (s, e) => {
                        this.downloadProgresses.Enqueue(new DownloadProgress(album.ArtworkUrl, e.BytesReceived));
                        UpdateProgress();
                    };

                    // Warn when downloaded
                    webClient.DownloadFileCompleted += (s, e) => {
                        if (!e.Cancelled && e.Error == null) {
                            artworkDownloaded = true;

                            // Convert/resize artwork to be saved in album folder
                            if (App.UserSettings.SaveCoverArtInFolder && (App.UserSettings.CoverArtInFolderConvertToJpg || App.UserSettings.CoverArtInFolderResize)) {
                                var settings = new ResizeSettings();
                                if (App.UserSettings.CoverArtInFolderConvertToJpg) {
                                    settings.Format = "jpg";
                                    settings.Quality = 90;
                                }
                                if (App.UserSettings.CoverArtInFolderResize) {
                                    settings.MaxHeight = App.UserSettings.CoverArtInFolderMaxSize;
                                    settings.MaxWidth = App.UserSettings.CoverArtInFolderMaxSize;
                                }
                                ImageBuilder.Current.Build(artworkTempPath, artworkFolderPath, settings); // Save it to the album folder
                            }

                            // Convert/resize artwork to be saved in tags
                            if (App.UserSettings.SaveCoverArtInTags && (App.UserSettings.CoverArtInTagsConvertToJpg || App.UserSettings.CoverArtInTagsResize)) {
                                var settings = new ResizeSettings();
                                if (App.UserSettings.CoverArtInTagsConvertToJpg) {
                                    settings.Format = "jpg";
                                    settings.Quality = 90;
                                }
                                if (App.UserSettings.CoverArtInTagsResize) {
                                    settings.MaxHeight = App.UserSettings.CoverArtInTagsMaxSize;
                                    settings.MaxWidth = App.UserSettings.CoverArtInTagsMaxSize;
                                }
                                ImageBuilder.Current.Build(artworkTempPath, artworkTempPath, settings); // Save it to %Temp%
                            }
                            artworkInTags = new TagLib.Picture(artworkTempPath) { Description = "Picture" };

                            try {
                                File.Delete(artworkTempPath);
                            } catch {
                                // Could not delete the file. Nevermind, it's in %Temp% folder...
                            }

                            // Note the file as downloaded
                            TrackFile currentFile = this.filesDownload.Where(f => f.Url == album.ArtworkUrl).First();
                            currentFile.Downloaded = true;
                            Log($"Downloaded artwork for album \"{album.Title}\"", LogType.IntermediateSuccess);
                        } else if (!e.Cancelled && e.Error != null) {
                            if (tries < App.UserSettings.DownloadMaxTries) {
                                Log($"Unable to download artwork for album \"{album.Title}\". Try {tries} of {App.UserSettings.DownloadMaxTries}", LogType.Warning);
                            } else {
                                Log($"Unable to download artwork for album \"{album.Title}\". Hit max retries of {App.UserSettings.DownloadMaxTries}", LogType.Error);
                            }
                        } // Else the download has been cancelled (by the user)

                        //doneEvent.Set();
                    };

                    //lock (this.pendingDownloads) {
                        if (this.userCancelled) {
                            // Abort
                            return null;
                        }
                        // Register current download
                        this.pendingDownloads.Add(webClient);
                        // Start download
                        await webClient.DownloadFileTaskAsync(new Uri(album.ArtworkUrl), artworkTempPath);
                    //}

                    // Wait for download to be finished
                    //doneEvent.WaitOne();
                    lock (this.pendingDownloads) {
                        this.pendingDownloads.Remove(webClient);
                    }
                }
            } while (!artworkDownloaded && tries < App.UserSettings.DownloadMaxTries);

            return artworkInTags;
        }

        /// <summary>
        /// Returns the albums located at the specified URLs.
        /// </summary>
        /// <param name="urls">The URLs.</param>
        private async Task<List<Album>> GetAlbumsAsync(List<String> urls) {
            var albums = new List<Album>();

            foreach (String url in urls) {
                Log($"Retrieving album data for {url}", LogType.Info);

                // Retrieve URL HTML source code
                String htmlCode = "";
                using (var webClient = new WebClient() { Encoding = Encoding.UTF8 }) {
                    if (webClient.Proxy != null) {
                        webClient.Proxy.Credentials = System.Net.CredentialCache.DefaultNetworkCredentials;
                    }

                    if (this.userCancelled) {
                        // Abort
                        return new List<Album>();
                    }

                    try {
                        htmlCode = await webClient.DownloadStringTaskAsync(url);
                    } catch {
                        Log($"Could not retrieve data for {url}", LogType.Error);
                        continue;
                    }
                }

                // Get info on album
                try {
                    albums.Add(BandcampHelper.GetAlbum(htmlCode));
                } catch {
                    Log($"Could not retrieve album info for {url}", LogType.Error);
                    continue;
                }
            }

            return albums;
        }

        /// <summary>
        /// Returns the artists discography from any URL (artist, album, track).
        /// </summary>
        /// <param name="urls">The URLs.</param>
        private async Task<List<String>> GetArtistDiscographyAsync(List<String> urls) {
            var albumsUrls = new List<String>();

            foreach (String url in urls) {
                Log($"Retrieving artist discography from {url}", LogType.Info);

                // Retrieve URL HTML source code
                String htmlCode = "";
                using (var webClient = new WebClient() { Encoding = Encoding.UTF8 }) {
                    if (webClient.Proxy != null) {
                        webClient.Proxy.Credentials = CredentialCache.DefaultNetworkCredentials;
                    }

                    if (this.userCancelled) {
                        // Abort
                        return new List<String>();
                    }

                    try {
                        htmlCode = await webClient.DownloadStringTaskAsync(url);
                    } catch {
                        Log($"Could not retrieve data for {url}", LogType.Error);
                        continue;
                    }
                }

                // Get artist "music" bandcamp page (http://artist.bandcamp.com/music)
                var regex = new Regex("band_url = \"(?<url>.*)\"");
                if (!regex.IsMatch(htmlCode)) {
                    Log($"No discography could be found on {url}. Try to uncheck the \"Download artist discography\" option", LogType.Error);
                    continue;
                }
                String artistMusicPage = regex.Match(htmlCode).Groups["url"].Value + "/music";

                // Retrieve artist "music" page HTML source code
                using (var webClient = new WebClient() { Encoding = Encoding.UTF8 }) {
                    if (webClient.Proxy != null) {
                        webClient.Proxy.Credentials = CredentialCache.DefaultNetworkCredentials;
                    }

                    if (this.userCancelled) {
                        // Abort
                        return new List<String>();
                    }

                    try {
                        htmlCode = await webClient.DownloadStringTaskAsync(artistMusicPage);
                    } catch {
                        Log($"Could not retrieve data for {artistMusicPage}", LogType.Error);
                        continue;
                    }
                }

                // Get albums referred on the page
                regex = new Regex("TralbumData.*\n.*url:.*'/music'\n");
                if (!regex.IsMatch(htmlCode)) {
                    // This seem to be a one-album artist with no "music" page => URL redirects to the unique album URL
                    albumsUrls.Add(url);
                } else {
                    // We are on a real "music" page
                    try {
                        albumsUrls.AddRange(BandcampHelper.GetAlbumsUrl(htmlCode));
                    } catch (NoAlbumFoundException) {
                        Log($"No referred album could be found on {artistMusicPage}. Try to uncheck the \"Download artist discography\" option", LogType.Error);
                        continue;
                    }
                }
            }

            return albumsUrls;
        }

        /// <summary>
        /// Replaces placeholders strings by the corresponding values in the specified filenameFormat path.
        /// </summary>
        /// <param name="album">The album currently downloaded.</param>
        /// <param name="track">The track currently downloaded.</param>
        private String GetFileName(Album album, Track track) {
            String fileName =
                App.UserSettings.FileNameFormat.Replace("{artist}", album.Artist)
                    .Replace("{title}", track.Title)
                    .Replace("{tracknum}", track.Number.ToString("00"));
            return fileName.ToAllowedFileName();
        }

        /// <summary>
        /// Returns the files to download from a list of albums.
        /// </summary>
        /// <param name="albums">The albums.</param>
        /// <param name="downloadCoverArt">True if the cover arts must be downloaded, false otherwise.</param>
        private async Task<List<TrackFile>> GetFilesToDownloadAsync(List<Album> albums, Boolean downloadCoverArt) {
            var files = new List<TrackFile>();
            foreach (Album album in albums) {
                Log($"Computing size for album \"{album.Title}\"...", LogType.Info);

                // Artwork
                if (downloadCoverArt && album.HasArtwork) {
                    long size = 0;
                    Boolean sizeRetrieved = false;
                    int tries = 0;
                    if (App.UserSettings.RetrieveFilesSize) {
                        do {
                            if (this.userCancelled) {
                                // Abort
                                return new List<TrackFile>();
                            }
                            await WaitForCooldownAsync(tries);
                            tries++;
                            try {
                                size = FileHelper.GetFileSize(album.ArtworkUrl, "HEAD");
                                sizeRetrieved = true;
                                Log($"Retrieved the size of the cover art file for album \"{album.Title}\"", LogType.VerboseInfo);
                            } catch {
                                sizeRetrieved = false;
                                if (tries < App.UserSettings.DownloadMaxTries) {
                                    Log($"Failed to retrieve the size of the cover art file for album \"{album.Title}\". Try {tries} of {App.UserSettings.DownloadMaxTries}", LogType.Warning);
                                } else {
                                    Log($"Failed to retrieve the size of the cover art file for album \"{album.Title}\". Hit max retries of {App.UserSettings.DownloadMaxTries}. Progress update may be wrong.", LogType.Error);
                                }
                            }
                        } while (!sizeRetrieved && tries < App.UserSettings.DownloadMaxTries);
                    }
                    files.Add(new TrackFile(album.ArtworkUrl, 0, size));
                }

                // Tracks
                foreach (Track track in album.Tracks) {
                    long size = 0;
                    Boolean sizeRetrieved = false;
                    int tries = 0;
                    if (App.UserSettings.RetrieveFilesSize)
                        do {
                            if (this.userCancelled) {
                                // Abort
                                return new List<TrackFile>();
                            }
                            await WaitForCooldownAsync(tries);
                            tries++;
                            try {
                                // Using the HEAD method on tracks urls does not work (Error 405: Method not allowed)
                                // Surprisingly, using the GET method does not seem to download the whole file, so we will use it to retrieve
                                // the mp3 sizes
                                size = FileHelper.GetFileSize(track.Mp3Url, "GET");
                                sizeRetrieved = true;
                                Log($"Retrieved the size of the MP3 file for the track \"{track.Title}\"", LogType.VerboseInfo);
                            } catch {
                                sizeRetrieved = false;
                                if (tries < App.UserSettings.DownloadMaxTries) {
                                    Log($"Failed to retrieve the size of the MP3 file for the track \"{track.Title}\". Try {tries} of {App.UserSettings.DownloadMaxTries}", LogType.Warning);
                                } else {
                                    Log($"Failed to retrieve the size of the MP3 file for the track \"{track.Title}\". Hit max retries of {App.UserSettings.DownloadMaxTries}. Progress update may be wrong.", LogType.Error);
                                }
                            }
                        } while (!sizeRetrieved && tries < App.UserSettings.DownloadMaxTries);
                    files.Add(new TrackFile(track.Mp3Url, 0, size));
                }
            }
            return files;
        }

        /// <summary>
        /// Initializes the logger component.
        /// </summary>
        private void InitializeLogger() {
            var fileTarget = new FileTarget() {
                FileName = Constants.LogFilePath,
                Layout = "${longdate}  ${level:uppercase=true:padding=-5:padCharacter= }  ${message}",
                ArchiveAboveSize = Constants.MaxLogSize,
                MaxArchiveFiles = 1,
            };

            var config = new LoggingConfiguration();
            config.AddRule(LogLevel.Trace, LogLevel.Fatal, fileTarget);

            LogManager.Configuration = config;
        }

        /// <summary>
        /// Logs to file and displays the specified message in the log textbox with the specified color.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="color">The color.</param>
        private void Log(String message, LogType logType) {
            // Log to file
            var logger = LogManager.GetCurrentClassLogger();
            logger.Log(logType.ToNLogLevel(), message);

            // Log to window
            if (App.UserSettings.ShowVerboseLog || logType == LogType.Error || logType == LogType.Info || logType == LogType.IntermediateSuccess || logType == LogType.Success) {
                this.Dispatcher.Invoke(new Action(() => {
                    // Time
                    var textRange = new TextRange(richTextBoxLog.Document.ContentEnd, richTextBoxLog.Document.ContentEnd) {
                        Text = DateTime.Now.ToString("HH:mm:ss") + " "
                    };
                    textRange.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.Gray);
                    // Message
                    textRange = new TextRange(richTextBoxLog.Document.ContentEnd, richTextBoxLog.Document.ContentEnd) {
                        Text = message
                    };
                    textRange.ApplyPropertyValue(TextElement.ForegroundProperty, LogHelper.GetColor(logType));
                    // Line break
                    richTextBoxLog.AppendText(Environment.NewLine);

                    if (richTextBoxLog.IsScrolledToEnd()) {
                        richTextBoxLog.ScrollToEnd();
                    }
                }));
            }
        }

        /// <summary>
        /// Replaces placeholders strings by the corresponding values in the specified download path.
        /// </summary>
        /// <param name="downloadPath">The download path to parse.</param>
        /// <param name="album">The album currently downloaded.</param>
        private String ParseDownloadPath(String downloadPath, Album album) {
            downloadPath = downloadPath.Replace("{year}", album.ReleaseDate.Year.ToString().ToAllowedFileName());
            downloadPath = downloadPath.Replace("{month}", album.ReleaseDate.Month.ToString().ToAllowedFileName());
            downloadPath = downloadPath.Replace("{day}", album.ReleaseDate.Day.ToString().ToAllowedFileName());
            downloadPath = downloadPath.Replace("{artist}", album.Artist.ToAllowedFileName());
            downloadPath = downloadPath.Replace("{album}", album.Title.ToAllowedFileName());
            return downloadPath;
        }

        /// <summary>
        /// Updates the state of the controls.
        /// </summary>
        /// <param name="downloadStarted">True if the download just started, false if it just stopped.</param>
        private void UpdateControlsState(Boolean downloadStarted) {
            this.Dispatcher.Invoke(new Action(() => {
                if (downloadStarted) {
                    // We just started the download
                    buttonBrowse.IsEnabled = false;
                    buttonStart.IsEnabled = false;
                    buttonStop.IsEnabled = true;
                    checkBoxDownloadDiscography.IsEnabled = false;
                    labelProgress.Content = "";
                    progressBar.IsIndeterminate = true;
                    progressBar.Value = progressBar.Minimum;
                    richTextBoxLog.Document.Blocks.Clear();
                    TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Indeterminate;
                    TaskbarItemInfo.ProgressValue = 0;
                    textBoxDownloadsPath.IsReadOnly = true;
                    textBoxUrls.IsReadOnly = true;
                } else {
                    // We just finished the download (or user has cancelled)
                    buttonBrowse.IsEnabled = true;
                    buttonStart.IsEnabled = true;
                    buttonStop.IsEnabled = false;
                    checkBoxDownloadDiscography.IsEnabled = true;
                    labelDownloadSpeed.Content = "";
                    progressBar.Foreground = new SolidColorBrush((Color) ColorConverter.ConvertFromString("#FF01D328")); // Green
                    progressBar.IsIndeterminate = false;
                    progressBar.Value = progressBar.Minimum;
                    TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;
                    TaskbarItemInfo.ProgressValue = 0;
                    textBoxDownloadsPath.IsReadOnly = false;
                    textBoxUrls.IsReadOnly = false;
                }
            }));
        }

        /// <summary>
        /// Updates the progress messages and the progressbar.
        /// </summary>
        private void UpdateProgress() {
            DateTime now = DateTime.Now;

            this.downloadProgresses.TryDequeue(out DownloadProgress downloadProgress);

            // Compute new progress values
            TrackFile currentFile = this.filesDownload.Where(f => f.Url == downloadProgress.FileUrl).First();
            currentFile.BytesReceived = downloadProgress.BytesReceived;
            long totalReceivedBytes = this.filesDownload.Sum(f => f.BytesReceived);
            long bytesToDownload = this.filesDownload.Sum(f => f.Size);
            Double downloadedFilesCount = this.filesDownload.Count(f => f.Downloaded);

            Double bytesPerSecond;
            if (this.lastTotalReceivedBytes == 0) {
                // First time we update the progress
                bytesPerSecond = 0;
                this.lastTotalReceivedBytes = totalReceivedBytes;
                this.lastDownloadSpeedUpdate = now;
            } else if ((now - this.lastDownloadSpeedUpdate).TotalMilliseconds > 500) {
                // Last update of progress happened more than 500 milliseconds ago
                // We only update the download speed every 500+ milliseconds
                bytesPerSecond =
                    ((Double) (totalReceivedBytes - this.lastTotalReceivedBytes)) /
                    (now - this.lastDownloadSpeedUpdate).TotalSeconds;
                this.lastTotalReceivedBytes = totalReceivedBytes;
                this.lastDownloadSpeedUpdate = now;

                // Update UI
                this.Dispatcher.Invoke(new Action(() => {
                    // Update download speed
                    labelDownloadSpeed.Content = (bytesPerSecond / 1024).ToString("0.0") + " kB/s";
                }));
            }

            // Update UI
            this.Dispatcher.Invoke(new Action(() => {
                if (!this.userCancelled) {
                    // Update progress label
                    labelProgress.Content =
                        ((Double) totalReceivedBytes / (1024 * 1024)).ToString("0.00") + " MB" +
                        (App.UserSettings.RetrieveFilesSize ? (" / " + ((Double) bytesToDownload / (1024 * 1024)).ToString("0.00") + " MB") : "");
                    if (App.UserSettings.RetrieveFilesSize) {
                        // Update progress bar based on bytes received
                        progressBar.Value = totalReceivedBytes;
                        // Taskbar progress is between 0 and 1
                        TaskbarItemInfo.ProgressValue = totalReceivedBytes / progressBar.Maximum;
                    } else {
                        // Update progress bar based on downloaded files
                        progressBar.Value = downloadedFilesCount;
                        // Taskbar progress is between 0 and count of files to download
                        TaskbarItemInfo.ProgressValue = downloadedFilesCount / progressBar.Maximum;
                    }
                }
            }));
        }

        private async Task WaitForCooldownAsync(int triesNumber) {
            if (App.UserSettings.DownloadRetryCooldown != 0) {
                await Task.Delay((int) ((Math.Pow(App.UserSettings.DownloadRetryExponent, triesNumber)) * App.UserSettings.DownloadRetryCooldown * 1000));
            }
        }

        #endregion

        #region Events

        private void ButtonBrowse_Click(object sender, RoutedEventArgs e) {
            var dialog = new System.Windows.Forms.FolderBrowserDialog {
                Description = "Select the folder to save albums"
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
                textBoxDownloadsPath.Text = dialog.SelectedPath + "\\{artist}\\{album}";
                // Force update of the settings file (it's not done unless the user give then lose focus on the textbox)
                textBoxDownloadsPath.GetBindingExpression(TextBox.TextProperty).UpdateSource();
            }
        }

        private void ButtonOpenSettingsWindow_Click(object sender, RoutedEventArgs e) {
            var windowSettings = new WindowSettings(activeDownloads) {
                Owner = this,
                ShowInTaskbar = false,
            };
            windowSettings.ShowDialog();
        }

        private async void ButtonStart_Click(object sender, RoutedEventArgs e) {
            if (textBoxUrls.Text == Constants.UrlsHint) {
                // No URL to look
                Log("Paste some albums URLs to be downloaded", LogType.Error);
                return;
            }

            await StartDownloadAsync();
        }

        private async Task StartDownloadAsync() {
            this.userCancelled = false;

            this.pendingDownloads = new List<WebClient>();

            // Set controls to "downloading..." state
            this.activeDownloads = true;
            UpdateControlsState(true);

            Log("Starting download...", LogType.Info);

            // Get user inputs
            List<String> userUrls = textBoxUrls.Text.Split(new String[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries).ToList();
            userUrls = userUrls.Distinct().ToList();

            var urls = new List<String>();
            var albums = new List<Album>();
            this.downloadProgresses = new ConcurrentQueue<DownloadProgress>();

            //Task.Factory.StartNew(() => {
            // Get URLs of albums to download
            if (App.UserSettings.DownloadArtistDiscography) {
                urls = await GetArtistDiscographyAsync(userUrls);
            } else {
                urls = userUrls;
            }
            urls = urls.Distinct().ToList();
            //}).ContinueWith(x => {
            // Get info on albums
            albums = await GetAlbumsAsync(urls);
            //}).ContinueWith(x => {
            // Save files to download (we'll need the list to update the progressBar)
            this.filesDownload = await GetFilesToDownloadAsync(albums, App.UserSettings.SaveCoverArtInTags || App.UserSettings.SaveCoverArtInFolder);
            //}).ContinueWith(x => {
            // Set progressBar max value
            long maxProgressBarValue;
            if (App.UserSettings.RetrieveFilesSize) {
                maxProgressBarValue = this.filesDownload.Sum(f => f.Size); // Bytes to download
            } else {
                maxProgressBarValue = this.filesDownload.Count; // Number of files to download
            }
            if (maxProgressBarValue > 0) {
                //this.Dispatcher.Invoke(new Action(() => {
                progressBar.IsIndeterminate = false;
                progressBar.Maximum = maxProgressBarValue;
                TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Normal;
                //}));
            }
            //}).ContinueWith(x => {
            // Start downloading albums
            if (App.UserSettings.DownloadOneAlbumAtATime) {
                // Download one album at a time
                foreach (Album album in albums) {
                    await DownloadAlbumAsync(album, ParseDownloadPath(App.UserSettings.DownloadsPath, album));
                }
            } else {
                // Parallel download
                //Task[] tasks = new Task[albums.Count];
                //for (int i = 0; i < albums.Count; i++) {
                //    Album album = albums[i]; // Mandatory or else => race condition
                //    tasks[i] = Task.Factory.StartNew(() =>
                //        DownloadAlbum(album, ParseDownloadPath(App.UserSettings.DownloadsPath, album)));
                //}
                //// Wait for all albums to be downloaded
                //Task.WaitAll(tasks);

                Int32[] indexes = Enumerable.Range(0, albums.Count).ToArray();
                await Task.WhenAll(indexes.Select(i => DownloadAlbumAsync(albums[i], ParseDownloadPath(App.UserSettings.DownloadsPath, albums[i]))));
            }
            //}).ContinueWith(x => {
            if (this.userCancelled) {
                // Display message if user cancelled
                Log("Downloads cancelled by user", LogType.Info);
            }
            // Set controls to "ready" state
            this.activeDownloads = false;
            UpdateControlsState(false);
            // Play a sound
            try {
                (new SoundPlayer(@"C:\Windows\Media\Windows Ding.wav")).Play();
            } catch {
            }
            //});
        }

        private void ButtonStop_Click(object sender, RoutedEventArgs e) {
            if (MessageBox.Show("Would you like to cancel all downloads?", "Bandcamp Downloader", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) {
                return;
            }

            this.userCancelled = true;
            Cursor = Cursors.Wait;
            Log("Cancelling downloads. Please wait...", LogType.Info);

            lock (this.pendingDownloads) {
                if (this.pendingDownloads.Count == 0) {
                    // Nothing to cancel
                    Cursor = Cursors.Arrow;
                    return;
                }
            }

            buttonStop.IsEnabled = false;
            progressBar.Foreground = System.Windows.Media.Brushes.Red;
            progressBar.IsIndeterminate = true;
            TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;
            TaskbarItemInfo.ProgressValue = 0;

            lock (this.pendingDownloads) {
                // Stop current downloads
                foreach (WebClient webClient in this.pendingDownloads) {
                    webClient.CancelAsync();
                }
            }

            Cursor = Cursors.Arrow;
        }

        private void LabelVersion_MouseDown(object sender, MouseButtonEventArgs e) {
            Process.Start(Constants.ProjectWebsite);
        }

        private void TextBoxUrls_GotFocus(object sender, RoutedEventArgs e) {
            if (textBoxUrls.Text == Constants.UrlsHint) {
                // Erase the hint message
                textBoxUrls.Text = "";
                textBoxUrls.Foreground = new SolidColorBrush(Colors.Black);
            }
        }

        private void TextBoxUrls_LostFocus(object sender, RoutedEventArgs e) {
            if (textBoxUrls.Text == "") {
                // Show the hint message
                textBoxUrls.Text = Constants.UrlsHint;
                textBoxUrls.Foreground = new SolidColorBrush(Colors.DarkGray);
            }
        }

        private void WindowMain_Closing(object sender, CancelEventArgs e) {
            if (this.activeDownloads) {
                // There are active downloads, ask for confirmation
                if (MessageBox.Show("There are currently active downloads. Are you sure you want to close the application and stop all downloads?", "Bandcamp Downloader", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.Cancel) {
                    // Cancel closing the window
                    e.Cancel = true;
                }
            }
        }

        #endregion
    }
}