#pragma warning disable 0169, 0414, 0649
namespace SeasonRotator.Client
{
	// How a setting is shown in the F12 window (BepInEx ConfigurationManager reads these fields by name)
	internal sealed class ConfigurationManagerAttributes
	{
		public bool? IsAdvanced;
		public int? Order;
		public System.Action<BepInEx.Configuration.ConfigEntryBase> CustomDrawer;
	}
}
