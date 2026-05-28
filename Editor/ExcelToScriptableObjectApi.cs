using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GreatClock.Common.ExcelToSO {

	[Serializable]
	public class ExcelToScriptableObjectImportOptions {
		public bool saveAssets = true;
		public bool suppressDialogs = true;
		public bool includeSlaves = true;
		public bool includeSkippedItems = false;
	}

	[Serializable]
	public class ExcelToScriptableObjectImportResult {
		public bool success;
		public int importedCount;
		public int failedCount;
		public int skippedCount;
		public List<ExcelToScriptableObjectImportItemResult> items = new List<ExcelToScriptableObjectImportItemResult>();

		public void Recalculate() {
			importedCount = 0;
			failedCount = 0;
			skippedCount = 0;
			for (int i = 0, imax = items == null ? 0 : items.Count; i < imax; i++) {
				ExcelToScriptableObjectImportItemResult item = items[i];
				if (item == null) { continue; }
				if (item.status == ExcelToScriptableObjectImportStatus.Imported) {
					importedCount++;
				} else if (item.status == ExcelToScriptableObjectImportStatus.Failed) {
					failedCount++;
				} else if (item.status == ExcelToScriptableObjectImportStatus.Skipped) {
					skippedCount++;
				}
			}
			success = failedCount == 0;
		}
	}

	public enum ExcelToScriptableObjectImportStatus {
		Imported,
		Failed,
		Skipped
	}

	[Serializable]
	public class ExcelToScriptableObjectImportItemResult {
		public ExcelToScriptableObjectImportStatus status;
		public string tableId;
		public string excelPath;
		public string assetPath;
		public string settingExcelPath;
		public string[] errors = new string[0];
		public string[] warnings = new string[0];
	}

	public static class ExcelToScriptableObjectApi {
		public static ExcelToScriptableObjectImportResult ImportAll(ExcelToScriptableObjectImportOptions options = null) {
			options = NormalizeOptions(options);
			bool settingsFileExists;
			List<ExcelToScriptableObjectSetting> settings = ExcelToScriptableObject.ReadSettingsForApi(out settingsFileExists);
			ExcelToScriptableObjectImportResult result = new ExcelToScriptableObjectImportResult();
			if (!settingsFileExists) {
				result.items.Add(FailedItem(null, null, "ExcelToSO settings not found: " + ExcelToScriptableObject.SETTINGS_PATH));
				result.Recalculate();
				return result;
			}
			for (int i = 0, imax = settings.Count; i < imax; i++) {
				AppendSettingImport(result, settings[i], settings[i], settings[i].excel_name, settings[i].asset_directory, options, false);
				if (options.includeSlaves && settings[i].slaves != null) {
					for (int j = 0, jmax = settings[i].slaves.Length; j < jmax; j++) {
						ExcelToScriptableObjectSlave slave = settings[i].slaves[j];
						if (slave == null) { continue; }
						AppendSettingImport(result, settings[i], settings[i], slave.excel_name, slave.asset_directory, options, false);
					}
				}
			}
			SaveAssetsIfNeeded(result, options);
			result.Recalculate();
			return result;
		}

		public static ExcelToScriptableObjectImportResult ImportExcelPaths(IEnumerable<string> excelPaths, ExcelToScriptableObjectImportOptions options = null) {
			options = NormalizeOptions(options);
			ExcelToScriptableObjectImportResult result = new ExcelToScriptableObjectImportResult();
			List<string> requested = NormalizeRequestedPaths(excelPaths);
			if (requested.Count == 0) {
				result.items.Add(FailedItem(null, null, "No excel paths were provided."));
				result.Recalculate();
				return result;
			}

			bool settingsFileExists;
			List<ExcelToScriptableObjectSetting> settings = ExcelToScriptableObject.ReadSettingsForApi(out settingsFileExists);
			if (!settingsFileExists) {
				for (int i = 0, imax = requested.Count; i < imax; i++) {
					result.items.Add(FailedItem(null, requested[i], "ExcelToSO settings not found: " + ExcelToScriptableObject.SETTINGS_PATH));
				}
				result.Recalculate();
				return result;
			}

			HashSet<string> matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			for (int i = 0, imax = settings.Count; i < imax; i++) {
				ExcelToScriptableObjectSetting setting = settings[i];
				if (MatchesRequested(setting.excel_name, requested)) {
					matched.Add(NormalizePathKey(setting.excel_name));
					AppendSettingImport(result, setting, setting, setting.excel_name, setting.asset_directory, options, false);
				}
				if (!options.includeSlaves || setting.slaves == null) { continue; }
				for (int j = 0, jmax = setting.slaves.Length; j < jmax; j++) {
					ExcelToScriptableObjectSlave slave = setting.slaves[j];
					if (slave == null || !MatchesRequested(slave.excel_name, requested)) { continue; }
					matched.Add(NormalizePathKey(slave.excel_name));
					AppendSettingImport(result, setting, setting, slave.excel_name, slave.asset_directory, options, false);
				}
			}

			for (int i = 0, imax = requested.Count; i < imax; i++) {
				string key = NormalizePathKey(requested[i]);
				if (!matched.Contains(key)) {
					result.items.Add(FailedItem(null, requested[i], "ExcelToSO settings does not contain this excel path: " + requested[i]));
				}
			}
			SaveAssetsIfNeeded(result, options);
			result.Recalculate();
			return result;
		}

		public static ExcelToScriptableObjectImportResult ImportBySettings(Predicate<ExcelToScriptableObjectSetting> filter, ExcelToScriptableObjectImportOptions options = null) {
			options = NormalizeOptions(options);
			ExcelToScriptableObjectImportResult result = new ExcelToScriptableObjectImportResult();
			bool settingsFileExists;
			List<ExcelToScriptableObjectSetting> settings = ExcelToScriptableObject.ReadSettingsForApi(out settingsFileExists);
			if (!settingsFileExists) {
				result.items.Add(FailedItem(null, null, "ExcelToSO settings not found: " + ExcelToScriptableObject.SETTINGS_PATH));
				result.Recalculate();
				return result;
			}
			for (int i = 0, imax = settings.Count; i < imax; i++) {
				ExcelToScriptableObjectSetting setting = settings[i];
				bool selected = filter == null || filter(setting);
				if (!selected) {
					if (options.includeSkippedItems) {
						result.items.Add(SkippedItem(setting.excel_name, ExcelToScriptableObject.GetAssetPathForApi(setting.excel_name, setting.asset_directory), setting.excel_name, "The setting did not match the import filter."));
					}
					continue;
				}
				AppendSettingImport(result, setting, setting, setting.excel_name, setting.asset_directory, options, false);
				if (!options.includeSlaves || setting.slaves == null) { continue; }
				for (int j = 0, jmax = setting.slaves.Length; j < jmax; j++) {
					ExcelToScriptableObjectSlave slave = setting.slaves[j];
					if (slave == null) { continue; }
					AppendSettingImport(result, setting, setting, slave.excel_name, slave.asset_directory, options, false);
				}
			}
			SaveAssetsIfNeeded(result, options);
			result.Recalculate();
			return result;
		}

		private static ExcelToScriptableObjectImportOptions NormalizeOptions(ExcelToScriptableObjectImportOptions options) {
			return options ?? new ExcelToScriptableObjectImportOptions();
		}

		private static void AppendSettingImport(
			ExcelToScriptableObjectImportResult result,
			ExcelToScriptableObjectSetting setting,
			ExcelToScriptableObjectSetting baseSetting,
			string excelPath,
			string assetDirectory,
			ExcelToScriptableObjectImportOptions options,
			bool requestedSkip) {
			if (string.IsNullOrEmpty(excelPath)) {
				result.items.Add(FailedItem(baseSetting == null ? null : baseSetting.excel_name, excelPath, "Excel path is empty."));
				return;
			}
			string assetPath = ExcelToScriptableObject.GetAssetPathForApi(excelPath, assetDirectory);
			if (requestedSkip) {
				result.items.Add(SkippedItem(excelPath, assetPath, baseSetting == null ? null : baseSetting.excel_name, "Skipped by request."));
				return;
			}
			if (!File.Exists(excelPath)) {
				result.items.Add(FailedItem(baseSetting == null ? null : baseSetting.excel_name, excelPath, "Excel file does not exist: " + excelPath));
				return;
			}
			if (!ExcelToScriptableObject.CheckProcessableForApi(setting, excelPath, assetDirectory)) {
				result.items.Add(FailedItem(baseSetting == null ? null : baseSetting.excel_name, excelPath, "ExcelToSO setting is not processable. Check namespace, script directory and asset directory."));
				return;
			}

			using (ExcelToScriptableObject.SuppressDialogsForApi(options.suppressDialogs))
			using (LogCapture capture = new LogCapture()) {
				bool ok = false;
				try {
					ok = ExcelToScriptableObject.FlushDataForApi(setting, excelPath, assetDirectory);
				} catch (Exception e) {
					capture.AddException(e);
				}
				ExcelToScriptableObjectImportItemResult item = new ExcelToScriptableObjectImportItemResult();
				item.status = ok && !capture.HasErrors ? ExcelToScriptableObjectImportStatus.Imported : ExcelToScriptableObjectImportStatus.Failed;
				item.tableId = Path.GetFileNameWithoutExtension(excelPath);
				item.excelPath = excelPath;
				item.assetPath = assetPath;
				item.settingExcelPath = baseSetting == null ? null : baseSetting.excel_name;
				item.errors = capture.Errors.ToArray();
				item.warnings = capture.Warnings.ToArray();
				if (item.status == ExcelToScriptableObjectImportStatus.Failed && item.errors.Length == 0) {
					item.errors = new string[] { "ExcelToSO import failed without a detailed error. Check the source xlsx and generated ScriptableObject class." };
				}
				result.items.Add(item);
			}
		}

		private static ExcelToScriptableObjectImportItemResult FailedItem(string settingExcelPath, string excelPath, string error) {
			return new ExcelToScriptableObjectImportItemResult() {
				status = ExcelToScriptableObjectImportStatus.Failed,
				tableId = string.IsNullOrEmpty(excelPath) ? null : Path.GetFileNameWithoutExtension(excelPath),
				excelPath = excelPath,
				assetPath = string.IsNullOrEmpty(excelPath) ? null : ExcelToScriptableObject.GetAssetPathForApi(excelPath, "Assets"),
				settingExcelPath = settingExcelPath,
				errors = new string[] { error },
				warnings = new string[0]
			};
		}

		private static ExcelToScriptableObjectImportItemResult SkippedItem(string excelPath, string assetPath, string settingExcelPath, string reason) {
			return new ExcelToScriptableObjectImportItemResult() {
				status = ExcelToScriptableObjectImportStatus.Skipped,
				tableId = string.IsNullOrEmpty(excelPath) ? null : Path.GetFileNameWithoutExtension(excelPath),
				excelPath = excelPath,
				assetPath = assetPath,
				settingExcelPath = settingExcelPath,
				errors = new string[0],
				warnings = new string[] { reason }
			};
		}

		private static List<string> NormalizeRequestedPaths(IEnumerable<string> excelPaths) {
			List<string> requested = new List<string>();
			if (excelPaths == null) { return requested; }
			foreach (string path in excelPaths) {
				if (string.IsNullOrEmpty(path)) { continue; }
				requested.Add(path);
			}
			return requested;
		}

		private static bool MatchesRequested(string excelPath, List<string> requested) {
			string key = NormalizePathKey(excelPath);
			for (int i = 0, imax = requested.Count; i < imax; i++) {
				if (NormalizePathKey(requested[i]) == key) { return true; }
			}
			return false;
		}

		private static string NormalizePathKey(string path) {
			if (string.IsNullOrEmpty(path)) { return string.Empty; }
			string normalized = path.Replace('\\', '/');
			try {
				normalized = Path.GetFullPath(normalized).Replace('\\', '/');
			} catch {
				// Keep the normalized relative path when the runtime cannot resolve it.
			}
			return normalized.TrimEnd('/');
		}

		private static void SaveAssetsIfNeeded(ExcelToScriptableObjectImportResult result, ExcelToScriptableObjectImportOptions options) {
			result.Recalculate();
			if (options.saveAssets && result.importedCount > 0) {
				AssetDatabase.SaveAssets();
			}
		}

		private sealed class LogCapture : IDisposable {
			public readonly List<string> Errors = new List<string>();
			public readonly List<string> Warnings = new List<string>();
			public bool HasErrors { get { return Errors.Count > 0; } }

			public LogCapture() {
				Application.logMessageReceived += OnLogMessageReceived;
			}

			public void AddException(Exception exception) {
				Errors.Add(exception == null ? "Unknown exception." : exception.ToString());
			}

			public void Dispose() {
				Application.logMessageReceived -= OnLogMessageReceived;
			}

			private void OnLogMessageReceived(string condition, string stackTrace, LogType type) {
				if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) {
					Errors.Add(string.IsNullOrEmpty(stackTrace) ? condition : condition + "\n" + stackTrace);
				} else if (type == LogType.Warning) {
					Warnings.Add(condition);
				}
			}
		}
	}
}
