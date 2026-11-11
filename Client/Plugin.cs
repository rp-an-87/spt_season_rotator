using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using SeasonRotator.Client.Patches;

namespace SeasonRotator.Client
{
	// Client companion of the Season Rotator server mod: makes the game draw the raid weather the server sends.
	[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
	public class Plugin : BaseUnityPlugin
	{
		public const string PluginGuid = "RPAN87.SeasonRotator.Client";
		public const string PluginName = "SeasonRotator.Client";
		public const string PluginVersion = "1.0.0";

		internal static ManualLogSource Log;
		internal static ConfigEntry<bool> DrawServerFog;
		internal static ConfigEntry<bool> LogWeather;

		private void Awake()
		{
			Log = Logger;

			// BepInEx rejects an apostrophe (and = " [ ] \) in section and key names: a bad key aborts the whole Awake
			DrawServerFog = Config.Bind("1. Weather", "1. Draw server fog in normal weather", true,
				"The game draws the fog of the raid weather (for example a Season Rotator weather event) only while the weather debug mode of the Time & Weather panel is on. With this on, the fog is also drawn in normal weather, the same way the panel does it. Affects all fog, natural fog too. Takes effect after a restart.");
			LogWeather = Config.Bind("2. Logging", "1. Log raid weather", true,
				"Writes to the BepInEx log the weather the game receives from the server when a raid loads (fog, rain, clouds, wind of the first node), and six times in the raid (every 5 seconds) the fog and rain it is drawing. For checking weather events. Takes effect after a restart.");

			Apply(new WeatherFogDrawPatch());
			Apply(new WeatherNodesPatch());
			Apply(new WeatherFogPatch());
		}

		// One failing patch must not stop the others
		private static void Apply(SPT.Reflection.Patching.ModulePatch patch)
		{
			try
			{
				patch.Enable();
			}
			catch (System.Exception e)
			{
				Log.LogError(patch.GetType().Name + " could not be applied: " + e);
			}
		}
	}
}
