using Rot.App.Models;

namespace Rot.App.Persistence;

public interface ISettingsStore
{
    Task<RotSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(RotSettings settings, CancellationToken cancellationToken = default);
    Task<RotSettings> ResetAsync(CancellationToken cancellationToken = default);
}
