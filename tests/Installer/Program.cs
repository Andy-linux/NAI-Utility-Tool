using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

if (args.Length != 2) throw new ArgumentException("Usage: SettingsProbe seed|check application-root");
string root = Path.GetFullPath(args[1]);
string bin = Path.Combine(root, "bin");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    string candidate = Path.Combine(bin, name.Name + ".dll");
    return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(bin, "NAI Utility Tool.dll"));
var type = assembly.GetType("NAITool.Services.SettingsService", throwOnError: true)!;
var service = Activator.CreateInstance(type, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
    binder: null, args: [Path.Combine(root, "user", "config")], culture: null)!;
object Get(object owner, string name) => owner.GetType().GetProperty(name)!.GetValue(owner)!;
void Set(object owner, string name, object value)
{
    var property = owner.GetType().GetProperty(name)!;
    property.SetValue(owner, Convert.ChangeType(value, property.PropertyType, CultureInfo.InvariantCulture));
}
// Synthetic credentials exercise the old and new DPAPI path without contacting any API.
const string fixtureToken = "INSTALLER-TEST-NOT-A-REAL-API-TOKEN";
if (args[0] == "seed")
{
    object settings = Get(service, "Settings");
    Set(settings, "ApiBaseUrl", "http://127.0.0.1:9");
    Set(settings, "HistorySidebarWidth", 317);
    Set(settings, "LanguageCode", "en_us");
    object parameters = Get(settings, "GenParameters");
    Set(parameters, "Model", "nai-diffusion-3");
    Set(parameters, "Seed", 424242);
    type.GetMethod("SetApiTokens")!.Invoke(service, [new[] { fixtureToken }]);
    type.GetMethod("Save", Type.EmptyTypes)!.Invoke(service, null);
}
else if (args[0] != "check") throw new ArgumentException("Unknown probe mode");
type.GetMethod("Load")!.Invoke(service, null);
var loaded = Get(service, "Settings");
var accounts = ((System.Collections.IEnumerable)Get(service, "Accounts")).Cast<object>().ToArray();
bool credential = accounts.Length == 1 && (string)Get(accounts[0], "Token") == fixtureToken;
bool values = (double)Get(loaded, "HistorySidebarWidth") == 317 &&
    (string)Get(loaded, "LanguageCode") == "en_us" &&
    (string)Get(loaded, "ApiBaseUrl") == "http://127.0.0.1:9" &&
    (string)Get(Get(loaded, "GenParameters"), "Model") == "nai-diffusion-3" &&
    Convert.ToString(Get(Get(loaded, "GenParameters"), "Seed"), CultureInfo.InvariantCulture) == "424242";
if (!credential || !values) throw new InvalidOperationException("Upgrade settings/credential compatibility failed");
Console.WriteLine(JsonSerializer.Serialize(new
{
    version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    settingsLoaded = values, credentialDecrypted = credential, networkRequests = 0
}));
