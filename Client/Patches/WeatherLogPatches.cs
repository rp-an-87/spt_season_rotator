using System.Reflection;
using EFT.Weather;
using SPT.Reflection.Patching;
using UnityEngine;

namespace SeasonRotator.Client.Patches
{
	// Diagnostics for weather events (Season Rotator): writes to the BepInEx log what the raid weather is when the game takes
	// it from the server, and a few times afterwards what the game is actually drawing. Read only, changes nothing.
	internal static class WeatherLog
	{
		internal static int Samples;
		internal static float NextSample;
	}

	// The raid start: the game hands the server's weather list to the weather controller
	internal class WeatherNodesPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(WeatherController).GetMethod(nameof(WeatherController.SetWeatherNodes), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPostfix]
		private static void Postfix(WeatherNode[] nodes)
		{
			if (!Plugin.LogWeather.Value)
			{
				return;
			}

			WeatherLog.Samples = 0;
			WeatherLog.NextSample = 0f;
			if (nodes == null || nodes.Length == 0)
			{
				Plugin.Log.LogInfo("[Weather] SetWeatherNodes: no nodes");
				return;
			}

			var first = nodes[0];
			Plugin.Log.LogInfo("[Weather] SetWeatherNodes: " + nodes.Length + " nodes, first node fog " + first.ScaterringFogDensity + ", rain " + first.Rain + ", rain randomness " + first.RainRandomness
				+ ", cloudness " + first.Cloudness + ", wind " + first.Wind + ", turbulence " + first.Turbulence + ", time " + first.Time);
		}
	}

	// What the game uses each frame to draw the sky: sampled every 5 seconds, 6 times per raid
	internal class WeatherFogPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(WeatherController).GetMethod("method_4", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPostfix]
		private static void Postfix(WeatherController __instance)
		{
			if (!Plugin.LogWeather.Value || WeatherLog.Samples >= 6 || Time.realtimeSinceStartup < WeatherLog.NextSample)
			{
				return;
			}

			WeatherLog.Samples++;
			WeatherLog.NextSample = Time.realtimeSinceStartup + 5f;
			var curve = __instance.WeatherCurve;
			Plugin.Log.LogInfo("[Weather] in raid: curve fog " + curve.Fog + ", rain " + curve.Rain + ", cloudiness " + curve.Cloudiness + ", sun height " + __instance.SunHeight
				+ ", weather debug " + (__instance.WeatherDebug != null && __instance.WeatherDebug.Enabled));
		}
	}
}
