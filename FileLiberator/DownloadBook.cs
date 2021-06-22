﻿using System;
using System.IO;
using System.Threading.Tasks;
using AudibleApi;
using DataLayer;
using Dinah.Core;
using Dinah.Core.ErrorHandling;
using FileManager;
using System.Net.Http;
using Dinah.Core.Net.Http;

namespace FileLiberator
{
    /// <summary>
    /// Download DRM book
    /// 
    /// Processes:
    /// Download: download aax file: the DRM encrypted audiobook
    /// Decrypt: remove DRM encryption from audiobook. Store final book
    /// Backup: perform all steps (downloaded, decrypt) still needed to get final book
    /// </summary>
    public class DownloadBook : DownloadableBase
    {
		private const string SERVICE_UNAVAILABLE = "Content Delivery Companion Service is not available.";

        public override bool Validate(LibraryBook libraryBook)
            => !AudibleFileStorage.Audio.Exists(libraryBook.Book.AudibleProductId)
            && !AudibleFileStorage.AAX.Exists(libraryBook.Book.AudibleProductId);

		public override async Task<StatusHandler> ProcessItemAsync(LibraryBook libraryBook)
		{
			var tempAaxFilename = getDownloadPath(libraryBook);
			var actualFilePath = await downloadAaxcBookAsync(libraryBook, tempAaxFilename);
			moveBook(libraryBook, actualFilePath);
			return verifyDownload(libraryBook);
		}

		private static string getDownloadPath(LibraryBook libraryBook)
			=> FileUtility.GetValidFilename(
				AudibleFileStorage.DownloadsInProgress,
				libraryBook.Book.Title,
				"aax",
				libraryBook.Book.AudibleProductId);

		private async Task<string> downloadAaxcBookAsync(LibraryBook libraryBook, string tempAaxFilename)
		{
			validate(libraryBook);

			var api = await GetApiAsync(libraryBook);

			var dlLic = await api.GetDownloadLicenseAsync(libraryBook.Book.AudibleProductId);

			var client = new HttpClient();
			client.DefaultRequestHeaders.Add("User-Agent", Resources.UserAgent);

			var actualFilePath = await PerformDownloadAsync(
				tempAaxFilename,
				(p) => client.DownloadFileAsync(new Uri(dlLic.DownloadUrl).AbsoluteUri, tempAaxFilename, p));

			System.Threading.Thread.Sleep(100);
			// if bad file download, a 0-33 byte file will be created
			// if service unavailable, a 52 byte string will be saved as file
			var length = new FileInfo(actualFilePath).Length;

			// success. save json and return
			if (length > 100)
			{
				// save along side book
				var jsonPath = PathLib.ReplaceExtension(actualFilePath, "json");
				var jsonContents = Newtonsoft.Json.JsonConvert.SerializeObject(dlLic, Newtonsoft.Json.Formatting.Indented);
				File.WriteAllText(jsonPath, jsonContents);

				return actualFilePath;
			}

			// else: failure. clean up and throw
			var contents = File.ReadAllText(actualFilePath);
			File.Delete(actualFilePath);

			var exMsg = contents.StartsWithInsensitive(SERVICE_UNAVAILABLE)
				? SERVICE_UNAVAILABLE
				: "Error downloading file";

			var ex = new Exception(exMsg);
			Serilog.Log.Logger.Error(ex, "Download error {@DebugInfo}", new
			{
				libraryBook.Book.Title,
				libraryBook.Book.AudibleProductId,
				libraryBook.Book.Locale,
				Account = libraryBook.Account?.ToMask() ?? "[empty]",
				tempAaxFilename,
				actualFilePath,
				length,
				contents
			});
			throw ex;
		}

		private static void validate(LibraryBook libraryBook)
		{
			string errorString(string field)
				=> $"{errorTitle()}\r\nCannot download book. {field} is not known. Try re-importing the account which owns this book.";

			string errorTitle()
			{
				var title
					= (libraryBook.Book.Title.Length > 53)
					? $"{libraryBook.Book.Title.Truncate(50)}..."
					: libraryBook.Book.Title;
				var errorBookTitle = $"{title} [{libraryBook.Book.AudibleProductId}]";
				return errorBookTitle;
			};

			if (string.IsNullOrWhiteSpace(libraryBook.Account))
				throw new Exception(errorString("Account"));

			if (string.IsNullOrWhiteSpace(libraryBook.Book.Locale))
				throw new Exception(errorString("Locale"));
		}

		private void moveBook(LibraryBook libraryBook, string actualFilePath)
		{
			var newAaxFilename = FileUtility.GetValidFilename(
				AudibleFileStorage.DownloadsFinal,
				libraryBook.Book.Title,
				"aax",
				libraryBook.Book.AudibleProductId);
			File.Move(actualFilePath, newAaxFilename);

			// also move DownloadLicense json file
			var jsonPathOld = PathLib.ReplaceExtension(actualFilePath, "json");
			var jsonPathNew = PathLib.ReplaceExtension(newAaxFilename, "json");
			File.Move(jsonPathOld, jsonPathNew);

			Invoke_StatusUpdate($"Successfully downloaded. Moved to: {newAaxFilename}");
		}

		private static StatusHandler verifyDownload(LibraryBook libraryBook)
			=> !AudibleFileStorage.AAX.Exists(libraryBook.Book.AudibleProductId)
			? new StatusHandler { "Downloaded AAX file cannot be found" }
			: new StatusHandler();
	}
}