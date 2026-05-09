using System.Threading;
using System.Threading.Tasks;
using OpenTDBLookup.Models;

namespace OpenTDBLookup.Services;

/// <summary>
/// Loads and saves the small set of user-visible toggles that need to
/// survive across app restarts. Storage format is a single JSON file next
/// to the executable; missing or corrupt files yield default-off settings.
/// </summary>
public interface ISettingsService
{
    AppSettings Current { get; }
    Task LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}
