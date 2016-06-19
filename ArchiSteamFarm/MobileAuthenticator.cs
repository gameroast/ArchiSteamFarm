﻿using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.JSON;
using HtmlAgilityPack;
using Newtonsoft.Json;

namespace ArchiSteamFarm {
	internal sealed class MobileAuthenticator {
		internal sealed class Confirmation {
			internal readonly uint ID;
			internal readonly ulong Key;

			internal Confirmation(uint id, ulong key) {
				if ((id == 0) || (key == 0)) {
					throw new ArgumentNullException(nameof(id) + " || " + nameof(key));
				}

				ID = id;
				Key = key;
			}
		}

		private static readonly byte[] TokenCharacters = { 50, 51, 52, 53, 54, 55, 56, 57, 66, 67, 68, 70, 71, 72, 74, 75, 77, 78, 80, 81, 82, 84, 86, 87, 88, 89 };
		private static readonly SemaphoreSlim TimeSemaphore = new SemaphoreSlim(1);

		private static short SteamTimeDifference;

		internal bool HasDeviceID => !string.IsNullOrEmpty(DeviceID);

		[JsonProperty(PropertyName = "shared_secret", Required = Required.DisallowNull)]
		private string SharedSecret;

		[JsonProperty(PropertyName = "identity_secret", Required = Required.DisallowNull)]
		private string IdentitySecret;

		[JsonProperty(PropertyName = "device_id")]
		private string DeviceID;

		private Bot Bot;

		internal static MobileAuthenticator LoadFromSteamGuardAccount(ObsoleteSteamGuardAccount sga) {
			if (sga != null) {
				return new MobileAuthenticator {
					SharedSecret = sga.SharedSecret,
					IdentitySecret = sga.IdentitySecret,
					DeviceID = sga.DeviceID
				};
			}

			Logging.LogNullError(nameof(sga));
			return null;
		}

		private MobileAuthenticator() {

		}

		internal void Init(Bot bot) {
			if (bot == null) {
				throw new ArgumentNullException(nameof(bot));
			}

			Bot = bot;
		}

		internal void CorrectDeviceID(string deviceID) {
			if (string.IsNullOrEmpty(deviceID)) {
				Logging.LogNullError(nameof(deviceID), Bot.BotName);
				return;
			}

			DeviceID = deviceID;
		}

		internal async Task<bool> HandleConfirmation(Confirmation confirmation, bool accept) {
			if (confirmation == null) {
				Logging.LogNullError(nameof(confirmation), Bot.BotName);
				return false;
			}

			uint time = await GetSteamTime().ConfigureAwait(false);
			if (time == 0) {
				Logging.LogNullError(nameof(time), Bot.BotName);
				return false;
			}

			string confirmationHash = GenerateConfirmationKey(time, "conf");
			if (!string.IsNullOrEmpty(confirmationHash)) {
				return await Bot.ArchiWebHandler.HandleConfirmation(DeviceID, confirmationHash, time, confirmation.ID, confirmation.Key, accept);
			}

			Logging.LogNullError(nameof(confirmationHash), Bot.BotName);
			return false;
		}

		internal async Task<Steam.ConfirmationDetails> GetConfirmationDetails(Confirmation confirmation) {
			if (confirmation == null) {
				Logging.LogNullError(nameof(confirmation), Bot.BotName);
				return null;
			}

			uint time = await GetSteamTime().ConfigureAwait(false);
			if (time == 0) {
				Logging.LogNullError(nameof(time), Bot.BotName);
				return null;
			}

			string confirmationHash = GenerateConfirmationKey(time, "conf");
			if (!string.IsNullOrEmpty(confirmationHash)) {
				return await Bot.ArchiWebHandler.GetConfirmationDetails(DeviceID, confirmationHash, time, confirmation.ID);
			}

			Logging.LogNullError(nameof(confirmationHash), Bot.BotName);
			return null;
		}

		internal async Task<string> GenerateToken() {
			uint time = await GetSteamTime().ConfigureAwait(false);
			if (time != 0) {
				return GenerateTokenForTime(time);
			}

			Logging.LogNullError(nameof(time), Bot.BotName);
			return null;
		}

		internal async Task<HashSet<Confirmation>> GetConfirmations() {
			uint time = await GetSteamTime().ConfigureAwait(false);
			if (time == 0) {
				Logging.LogNullError(nameof(time), Bot.BotName);
				return null;
			}

			string confirmationHash = GenerateConfirmationKey(time, "conf");
			if (string.IsNullOrEmpty(confirmationHash)) {
				Logging.LogNullError(nameof(confirmationHash), Bot.BotName);
				return null;
			}

			HtmlDocument htmlDocument = await Bot.ArchiWebHandler.GetConfirmations(DeviceID, confirmationHash, time);
			if (htmlDocument == null) {
				return null;
			}

			HtmlNodeCollection confirmations = htmlDocument.DocumentNode.SelectNodes("//div[@class='mobileconf_list_entry']");
			if (confirmations == null) {
				return null;
			}

			HashSet<Confirmation> result = new HashSet<Confirmation>();
			foreach (HtmlNode confirmation in confirmations) {
				string idString = confirmation.GetAttributeValue("data-confid", null);
				if (string.IsNullOrEmpty(idString)) {
					Logging.LogNullError(nameof(idString), Bot.BotName);
					continue;
				}

				uint id;
				if (!uint.TryParse(idString, out id) || (id == 0)) {
					Logging.LogNullError(nameof(id), Bot.BotName);
					continue;
				}

				string keyString = confirmation.GetAttributeValue("data-key", null);
				if (string.IsNullOrEmpty(keyString)) {
					Logging.LogNullError(nameof(keyString), Bot.BotName);
					continue;
				}

				ulong key;
				if (!ulong.TryParse(keyString, out key) || (key == 0)) {
					Logging.LogNullError(nameof(key), Bot.BotName);
					continue;
				}

				result.Add(new Confirmation(id, key));
			}

			return result;
		}

		internal async Task<uint> GetSteamTime() {
			if (SteamTimeDifference != 0) {
				return (uint) (Utilities.GetUnixTime() + SteamTimeDifference);
			}

			await TimeSemaphore.WaitAsync().ConfigureAwait(false);

			if (SteamTimeDifference == 0) {
				uint serverTime = Bot.ArchiWebHandler.GetServerTime();
				if (serverTime != 0) {
					SteamTimeDifference = (short) (serverTime - Utilities.GetUnixTime());
				}
			}

			TimeSemaphore.Release();
			return (uint) (Utilities.GetUnixTime() + SteamTimeDifference);
		}

		private string GenerateTokenForTime(long time) {
			if (time == 0) {
				Logging.LogNullError(nameof(time), Bot.BotName);
				return null;
			}

			byte[] sharedSecretArray = Convert.FromBase64String(SharedSecret);
			byte[] timeArray = new byte[8];

			time /= 30L;

			for (int i = 8; i > 0; i--) {
				timeArray[i - 1] = (byte) time;
				time >>= 8;
			}

			byte[] hashedData;
			using (HMACSHA1 hmacGenerator = new HMACSHA1(sharedSecretArray, true)) {
				hashedData = hmacGenerator.ComputeHash(timeArray);
			}

			byte b = (byte) (hashedData[19] & 0xF);
			int codePoint = ((hashedData[b] & 0x7F) << 24) | ((hashedData[b + 1] & 0xFF) << 16) | ((hashedData[b + 2] & 0xFF) << 8) | (hashedData[b + 3] & 0xFF);

			byte[] codeArray = new byte[5];
			for (int i = 0; i < 5; ++i) {
				codeArray[i] = TokenCharacters[codePoint % TokenCharacters.Length];
				codePoint /= TokenCharacters.Length;
			}

			return Encoding.UTF8.GetString(codeArray);
		}

		private string GenerateConfirmationKey(uint time, string tag = null) {
			if (time == 0) {
				Logging.LogNullError(nameof(time), Bot.BotName);
				return null;
			}

			byte[] b64Secret = Convert.FromBase64String(IdentitySecret);

			int bufferSize = 8;
			if (string.IsNullOrEmpty(tag) == false) {
				bufferSize += Math.Min(32, tag.Length);
			}

			byte[] buffer = new byte[bufferSize];

			byte[] timeArray = BitConverter.GetBytes((long) time);
			if (BitConverter.IsLittleEndian) {
				Array.Reverse(timeArray);
			}

			Array.Copy(timeArray, buffer, 8);
			if (string.IsNullOrEmpty(tag) == false) {
				Array.Copy(Encoding.UTF8.GetBytes(tag), 0, buffer, 8, bufferSize - 8);
			}

			byte[] hash;
			using (HMACSHA1 hmac = new HMACSHA1(b64Secret, true)) {
				hash = hmac.ComputeHash(buffer);
			}

			return Convert.ToBase64String(hash, Base64FormattingOptions.None);
		}
	}
}
