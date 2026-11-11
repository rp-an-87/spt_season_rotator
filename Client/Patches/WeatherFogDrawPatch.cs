using System.Reflection;
using EFT.Weather;
using SPT.Reflection.Patching;

namespace SeasonRotator.Client.Patches
{
	// The game only gives the classic fog renderer (TOD_Scattering) its density while the weather debug mode is on (the F13
	// Time & Weather panel turns that on); in normal weather it only feeds the other renderer (MBOIT_Scattering), which on
	// this setup does not show the server's fog. This does the same assignment in normal weather, so the fog of a weather
	// event (Season Rotator) is drawn like the fog set in the panel.
	internal class WeatherFogDrawPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(WeatherController).GetMethod(nameof(WeatherController.ControlScattering), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPostfix]
		private static void Postfix(WeatherController __instance, TOD_Scattering ___tod_Scattering_0, float fog)
		{
			if (!Plugin.DrawServerFog.Value || ___tod_Scattering_0 == null || __instance.WeatherDebug.Enabled)
			{
				return;
			}

			___tod_Scattering_0.GlobalDensity = fog;
		}
	}
}
