using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using EFT.Communications;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using UnityEngine;

namespace SeasonRotator.Client
{
	// F12 controls for the Season Rotator server mod: set the season, skip it, force a weather event. Each picker is a row with
	// previous / next buttons around the choice and the button that sends it, so the choice and the action sit side by side.
	internal static class Control
	{
		private const string Section = "3. Control";
		private static readonly string[] FallbackSeasons = { "SUMMER", "AUTUMN", "AUTUMN_LATE", "STORM", "WINTER", "SPRING_EARLY", "SPRING" };

		private static JObject _info;
		private static bool _loaded;
		private static string _loadError;

		internal static void Setup(ConfigFile config)
		{
			config.Bind(Section, "1. Status", "",
				new ConfigDescription("Needs the SeasonRotator server mod. Shows the season, the planned event and the forced event; Refresh asks the server again.", null,
					new ConfigurationManagerAttributes { CustomDrawer = DrawStatus }));

			config.Bind(Section, "2. Season", "",
				new ConfigDescription("Pick a season with the arrows and press the button to start it now. Its raid counter starts at 0, like a skip. A season that is switched off in the server config (length 0) ends after its first raid.", null,
					new ConfigurationManagerAttributes { CustomDrawer = entry => DrawPicker("season", SeasonNames, () => _info?.Value<string>("season"), "Set season") }));

			config.Bind(Section, "3. Skip season", "",
				new ConfigDescription("Ends the current season now and moves to the next one that is switched on.", null,
					new ConfigurationManagerAttributes { CustomDrawer = DrawSkip }));

			config.Bind(Section, "4. Event", "",
				new ConfigDescription("Pick a weather event with the arrows and press the button to force it on your next outdoor raid (Factory, Labs and Labyrinth have no weather and are skipped). It is an extra: the events the server planned for the season stay as they are, and it ignores the season an event is meant for. No Jaeger message. The list comes from customWeather.json on the server.", null,
					new ConfigurationManagerAttributes { CustomDrawer = entry => DrawPicker("event", EventNames, () => null, "Force event") }));
		}

		private static List<string> SeasonNames()
		{
			var list = _info?["seasons"]?.ToObject<List<string>>();
			return list != null && list.Count > 0 ? list : FallbackSeasons.ToList();
		}

		private static List<string> EventNames() => _info?["events"]?.ToObject<List<string>>() ?? new List<string>();

		private static void EnsureLoaded()
		{
			if (!_loaded)
			{
				Refresh();
			}
		}

		private static void Refresh()
		{
			_loaded = true;
			try
			{
				string json = RequestHandler.GetJson("/seasonrotator/info");
				_info = string.IsNullOrEmpty(json) ? null : JObject.Parse(json);
				_loadError = _info == null ? "no answer from the server mod" : null;
			}
			catch (Exception e)
			{
				_info = null;
				_loadError = e.Message;
			}
		}

		private static void DrawStatus(ConfigEntryBase entry)
		{
			EnsureLoaded();
			GUILayout.BeginHorizontal();
			GUILayout.Label(StatusText(), GUILayout.ExpandWidth(true));
			if (GUILayout.Button("Refresh", GUILayout.Width(80)))
			{
				Refresh();
			}

			GUILayout.EndHorizontal();
		}

		private static string StatusText()
		{
			if (_info == null)
			{
				return "Server mod not reachable: " + (_loadError ?? "no answer");
			}

			string text = _info.Value<string>("season") + ", raid " + _info.Value<int>("raidsInSeason") + "/" + _info.Value<int>("seasonLength");
			var planned = _info["plannedEventRaids"]?.ToObject<List<int>>();
			if (planned != null && planned.Count > 0)
			{
				text += "  |  planned event on raid " + planned[0] + " of the season group";
			}

			string forced = _info.Value<string>("forcedEvent");
			text += "  |  forced: " + (string.IsNullOrEmpty(forced) ? "none" : forced);
			return text;
		}

		private static void DrawSkip(ConfigEntryBase entry)
		{
			if (GUILayout.Button("Skip to the next season", GUILayout.ExpandWidth(true)))
			{
				Send("skip", null);
			}
		}

		// The choice of each picker lives here, not in the config, so F12 does not show it as a changed setting
		private static readonly Dictionary<string, string> Selected = new Dictionary<string, string>();
		private static GUIStyle _choiceStyle;

		private static void DrawPicker(string action, Func<List<string>> names, Func<string> defaultChoice, string buttonLabel)
		{
			EnsureLoaded();
			List<string> options = names();
			Selected.TryGetValue(action, out string current);
			int index = current == null ? -1 : options.IndexOf(current);
			if (index < 0 && options.Count > 0)
			{
				index = Math.Max(0, options.IndexOf(defaultChoice() ?? string.Empty));
				Selected[action] = options[index];
			}

			if (_choiceStyle == null)
			{
				_choiceStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
			}

			GUILayout.BeginHorizontal();
			GUI.enabled = options.Count > 1;
			if (GUILayout.Button("<", GUILayout.Width(28)))
			{
				Selected[action] = options[(index + options.Count - 1) % options.Count];
			}

			GUILayout.Label(options.Count == 0 ? "(no list from the server)" : options[index], _choiceStyle, GUILayout.ExpandWidth(true));
			if (GUILayout.Button(">", GUILayout.Width(28)))
			{
				Selected[action] = options[(index + 1) % options.Count];
			}

			GUI.enabled = options.Count > 0;
			if (GUILayout.Button(buttonLabel, GUILayout.Width(110)))
			{
				Send(action, options[index]);
			}

			GUI.enabled = true;
			GUILayout.EndHorizontal();
		}

		private static void Send(string action, string value)
		{
			try
			{
				string body = new JObject { ["action"] = action, ["value"] = value }.ToString(Formatting.None);
				string json = RequestHandler.PostJson("/seasonrotator/do", body);
				JObject answer = string.IsNullOrEmpty(json) ? null : JObject.Parse(json);
				if (answer != null && (answer.Value<bool?>("ok") ?? false))
				{
					Plugin.Log.LogInfo("[Control] " + answer.Value<string>("message"));
					NotificationManager.DisplayMessageNotification("Season Rotator: " + answer.Value<string>("message"));
					Refresh();
					return;
				}

				string reason = answer?.Value<string>("error") ?? "no answer from the server mod";
				Plugin.Log.LogWarning("[Control] " + action + " failed: " + reason);
				NotificationManager.DisplayWarningNotification("Season Rotator: " + reason);
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning("[Control] " + action + " request failed: " + e.Message);
				NotificationManager.DisplayWarningNotification("Season Rotator: " + e.Message);
			}
		}
	}
}
