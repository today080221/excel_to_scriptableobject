using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GreatClock.Common.ExcelToSO.Tests {

	public class ExcelToScriptableObjectApiTests {
		private const string TempRoot = "Assets/ExcelToSoApiTests";
		private const string TempGenerated = TempRoot + "/Generated";

		private string mPreviousSettings;
		private bool mHadPreviousSettings;

		[SetUp]
		public void SetUp() {
			mHadPreviousSettings = File.Exists(ExcelToScriptableObject.SETTINGS_PATH);
			mPreviousSettings = mHadPreviousSettings ? File.ReadAllText(ExcelToScriptableObject.SETTINGS_PATH, Encoding.UTF8) : null;
			Directory.CreateDirectory(TempRoot);
			Directory.CreateDirectory(TempGenerated);
			File.WriteAllText(TempRoot + "/Broken.xlsx", "not a real xlsx", Encoding.UTF8);
			AssetDatabase.Refresh();
		}

		[TearDown]
		public void TearDown() {
			if (mHadPreviousSettings) {
				File.WriteAllText(ExcelToScriptableObject.SETTINGS_PATH, mPreviousSettings, Encoding.UTF8);
			} else if (File.Exists(ExcelToScriptableObject.SETTINGS_PATH)) {
				File.Delete(ExcelToScriptableObject.SETTINGS_PATH);
			}
			if (Directory.Exists(TempRoot)) {
				Directory.Delete(TempRoot, true);
			}
			AssetDatabase.Refresh();
		}

		[Test]
		public void ImportExcelPathsTargetsOnlyRequestedExactExcelPath() {
			string requested = TempRoot + "/Broken.xlsx";
			string unrequested = TempRoot + "/Unrequested.xlsx";
			WriteSettings(requested, unrequested);

			ExcelToScriptableObjectImportResult result = ExcelToScriptableObjectApi.ImportExcelPaths(new[] { requested });

			Assert.That(result.items.Select(item => item.excelPath), Is.EquivalentTo(new[] { requested }));
			Assert.That(result.failedCount, Is.EqualTo(1));
			Assert.That(result.items[0].errors.Length, Is.GreaterThan(0));
		}

		[Test]
		public void MissingSettingsReturnsClearError() {
			if (File.Exists(ExcelToScriptableObject.SETTINGS_PATH)) {
				File.Delete(ExcelToScriptableObject.SETTINGS_PATH);
			}

			ExcelToScriptableObjectImportResult result = ExcelToScriptableObjectApi.ImportExcelPaths(new[] { TempRoot + "/Broken.xlsx" });

			Assert.That(result.success, Is.False);
			Assert.That(result.failedCount, Is.EqualTo(1));
			Assert.That(result.items[0].errors[0], Does.Contain("settings not found"));
		}

		[Test]
		public void OneBadRequestedTableDoesNotHideOtherRequestedFailures() {
			string broken = TempRoot + "/Broken.xlsx";
			string missing = TempRoot + "/Missing.xlsx";
			WriteSettings(broken, missing);

			ExcelToScriptableObjectImportResult result = ExcelToScriptableObjectApi.ImportExcelPaths(new[] { broken, missing });

			Assert.That(result.success, Is.False);
			Assert.That(result.failedCount, Is.EqualTo(2));
			Assert.That(result.items.Select(item => item.excelPath), Is.EquivalentTo(new[] { broken, missing }));
			Assert.That(result.items.All(item => item.errors.Length > 0), Is.True);
		}

		[Test]
		public void LegacySingleProfileLoadsAsDefaultProfile() {
			string requested = TempRoot + "/Broken.xlsx";
			WriteSettings(requested);

			ExcelToScriptableObjectImportResult result = ExcelToScriptableObjectApi.ImportByProfile(ExcelToScriptableObject.DEFAULT_PROFILE_ID);

			Assert.That(result.profileId, Is.EqualTo(ExcelToScriptableObject.DEFAULT_PROFILE_ID));
			Assert.That(result.items.Select(item => item.excelPath), Is.EquivalentTo(new[] { requested }));
		}

		[Test]
		public void ImportByProfileUsesSourceOfTruthCacheWithoutChangingUiSelection() {
			string previousProfile = EditorPrefs.GetString("excel_to_scriptableobject.active_profile", "");
			EditorPrefs.SetString("excel_to_scriptableobject.active_profile", ExcelToScriptableObject.DEFAULT_PROFILE_ID);
			try {
				string local = TempRoot + "/Local.xlsx";
				string cache = TempRoot + "/Cache.xlsx";
				WriteProfileSettings(local, cache);

				ExcelToScriptableObjectImportResult result = ExcelToScriptableObjectApi.ImportByProfile(ExcelToScriptableObject.SOURCE_OF_TRUTH_CACHE_PROFILE_ID);

				Assert.That(result.profileId, Is.EqualTo(ExcelToScriptableObject.SOURCE_OF_TRUTH_CACHE_PROFILE_ID));
				Assert.That(result.items.Select(item => item.excelPath), Is.EquivalentTo(new[] { cache }));
				Assert.That(EditorPrefs.GetString("excel_to_scriptableobject.active_profile", ""), Is.EqualTo(ExcelToScriptableObject.DEFAULT_PROFILE_ID));
			} finally {
				if (string.IsNullOrEmpty(previousProfile)) {
					EditorPrefs.DeleteKey("excel_to_scriptableobject.active_profile");
				} else {
					EditorPrefs.SetString("excel_to_scriptableobject.active_profile", previousProfile);
				}
			}
		}

		[Test]
		public void FieldTypeParserAcceptsSourceOfTruthAliases() {
			AssertFieldType("integer", "Int");
			AssertFieldType("boolean", "Bool");
			AssertFieldType("text", "String");
			AssertFieldType("number", "Float");
			AssertFieldType("integer[]", "Ints");
			AssertFieldType("integers", "Ints");
			AssertFieldType("[integer]", "Ints");
			AssertFieldType("number[]", "Floats");
			AssertFieldType("numbers", "Floats");
			AssertFieldType("[number]", "Floats");
			AssertFieldType("text[]", "Strings");
			AssertFieldType("[text]", "Strings");
		}

		[Test]
		public void JsonFieldTypeErrorTellsUserToChooseConcreteExcelToSoType() {
			MethodInfo method = typeof(ExcelToScriptableObject).GetMethod("BuildJsonFieldTypeError", BindingFlags.NonPublic | BindingFlags.Static);
			Assert.That(method, Is.Not.Null);

			string message = (string)method.Invoke(null, new object[] { "Assets/ConfigCache/NpcUnitData.xlsx", "NpcUnitData", 19, 1, "json" });

			Assert.That(message, Does.Contain("NpcUnitData!T2"));
			Assert.That(message, Does.Contain("json"));
			Assert.That(message, Does.Contain("int[]"));
			Assert.That(message, Does.Contain("float[]"));
			Assert.That(message, Does.Contain("string[]"));
			Assert.That(message, Does.Contain("string"));
		}

		private static void WriteSettings(params string[] excelPaths) {
			ExcelToScriptableObjectSetting[] settings = excelPaths.Select(path => new ExcelToScriptableObjectSetting() {
				excel_name = path,
				script_directory = TempGenerated,
				asset_directory = TempGenerated,
				name_space = "GreatClock.Common.ExcelToSO.Tests",
				slaves = new ExcelToScriptableObjectSlave[0]
			}).ToArray();
			ExcelToScriptableObjectSettings data = new ExcelToScriptableObjectSettings() {
				configs = new ExcelToScriptableObjectGlobalConfigs(),
				excels = settings
			};
			File.WriteAllText(ExcelToScriptableObject.SETTINGS_PATH, JsonUtility.ToJson(data, true), Encoding.UTF8);
		}

		private static void WriteProfileSettings(string localPath, string cachePath) {
			ExcelToScriptableObjectSettings data = new ExcelToScriptableObjectSettings() {
				configs = new ExcelToScriptableObjectGlobalConfigs(),
				excels = new[] { BuildSetting(localPath) },
				profiles = new[] {
					new ExcelToScriptableObjectProfile() {
						profile_id = ExcelToScriptableObject.DEFAULT_PROFILE_ID,
						display_name = "本地 Excel",
						input_root = "Excel/",
						configs = new ExcelToScriptableObjectGlobalConfigs(),
						excels = new[] { BuildSetting(localPath) }
					},
					new ExcelToScriptableObjectProfile() {
						profile_id = ExcelToScriptableObject.SOURCE_OF_TRUTH_CACHE_PROFILE_ID,
						display_name = "Source of Truth cache",
						input_root = ".config-sheet-forge/excel-cache/",
						source_of_truth_cache = true,
						configs = new ExcelToScriptableObjectGlobalConfigs(),
						excels = new[] { BuildSetting(cachePath) }
					}
				}
			};
			File.WriteAllText(ExcelToScriptableObject.SETTINGS_PATH, JsonUtility.ToJson(data, true), Encoding.UTF8);
		}

		private static ExcelToScriptableObjectSetting BuildSetting(string path) {
			return new ExcelToScriptableObjectSetting() {
				excel_name = path,
				script_directory = TempGenerated,
				asset_directory = TempGenerated,
				name_space = "GreatClock.Common.ExcelToSO.Tests",
				slaves = new ExcelToScriptableObjectSlave[0]
			};
		}

		private static void AssertFieldType(string token, string expectedEnumName) {
			MethodInfo method = typeof(ExcelToScriptableObject).GetMethod("GetFieldType", BindingFlags.NonPublic | BindingFlags.Static);
			Assert.That(method, Is.Not.Null);
			object value = method.Invoke(null, new object[] { token });
			Assert.That(value.ToString(), Is.EqualTo(expectedEnumName));
		}
	}
}
