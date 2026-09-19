using EntraGuard.MediaService.Auth;
using EntraGuard.MediaService.Persistence;

namespace EntraGuard.MediaService.Sessions;

public sealed record UserPreferences(int Version = 1, string PreferredChannel = "teams", bool VerificationNotifications = true,
    bool ApprovalNotifications = true, DateTimeOffset? UpdatedAt = null);
public sealed record InboxNotification(string Id, string Title, string Detail, DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt = null, string Channel = "in-app");

public sealed class PreferenceService(IStateStore store, TimeProvider time, TenantPolicyService policies)
{
    public static string Row(Owner owner) => owner.Row("preferences", "current");
    public async Task<UserPreferences> GetAsync(Owner owner, CancellationToken ct) =>
        (await store.ReadAsync(owner.TenantId, Row(owner), ct))?.Value<UserPreferences>() ?? new();
    public async Task<string?> SaveAsync(Owner owner, UserPreferences input, CancellationToken ct)
    {
        if (input.PreferredChannel is not ("teams" or "browser" or "phone")) return "Choose a supported channel.";
        if (!(await policies.GetAsync(owner.TenantId, ct)).Channels.Contains(input.PreferredChannel)) return "Your tenant does not allow this channel.";
        var row = await store.ReadAsync(owner.TenantId, Row(owner), ct);
        var current = row?.Value<UserPreferences>() ?? new();
        if (input.Version != current.Version) return "Preferences changed. Refresh before saving.";
        return await store.CommitAsync(owner.TenantId, [StateWrite.Put(Row(owner), input with
            { Version = current.Version + 1, UpdatedAt = time.GetUtcNow() }, row?.Version)], ct)
            ? null : "Preferences changed. Refresh before saving.";
    }

    public static StateWrite Notification(Owner owner, string key, string title, string detail, DateTimeOffset at)
    {
        var id = $"{DateTimeOffset.MaxValue.UtcTicks - at.UtcTicks:D19}_{key}";
        return StateWrite.Put(owner.Row("notification", id), new InboxNotification(id, title, detail, at));
    }
    public Task<StatePage> InboxAsync(Owner owner, string? cursor, CancellationToken ct) =>
        store.ListAsync(owner.TenantId, owner.Prefix("notification"), 20, cursor, ct);
    public async Task<bool> MarkReadAsync(Owner owner, string id, CancellationToken ct)
    {
        var row = await store.ReadAsync(owner.TenantId, owner.Row("notification", id), ct);
        if (row is null) return false;
        var item = row.Value<InboxNotification>();
        return item.ReadAt is not null || await store.CommitAsync(owner.TenantId,
            [StateWrite.Put(row.Id, item with { ReadAt = time.GetUtcNow() }, row.Version)], ct);
    }
}
