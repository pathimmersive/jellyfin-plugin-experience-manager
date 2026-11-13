using System;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;

namespace Jellyfin.Plugin.ExperienceManager.Audiences;

/// <summary>
/// Records when an account is created, since Jellyfin keeps no creation time, so New users can tell it is new.
/// </summary>
public sealed class UserCreatedRecorder : IEventConsumer<UserCreatedEventArgs>
{
    private readonly AudienceStore _audiences;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserCreatedRecorder"/> class.
    /// </summary>
    /// <param name="audiences">The audiences.</param>
    public UserCreatedRecorder(AudienceStore audiences)
        : this(audiences, TimeProvider.System)
    {
    }

    internal UserCreatedRecorder(AudienceStore audiences, TimeProvider time)
    {
        _audiences = audiences;
        _time = time;
    }

    /// <inheritdoc />
    public Task OnEvent(UserCreatedEventArgs eventArgs)
    {
        _audiences.RecordCreated(eventArgs.Argument.Id, _time.GetUtcNow());
        return Task.CompletedTask;
    }
}

/// <summary>
/// Forgets a deleted account's creation time, memberships and dismissed announcements.
/// </summary>
public sealed class UserDeletedRecorder : IEventConsumer<UserDeletedEventArgs>
{
    private readonly AudienceStore _audiences;
    private readonly Announcements.DismissalStore? _dismissals;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserDeletedRecorder"/> class.
    /// </summary>
    /// <param name="audiences">The audiences.</param>
    /// <param name="dismissals">The announcements users dismissed.</param>
    public UserDeletedRecorder(AudienceStore audiences, Announcements.DismissalStore dismissals)
        : this(audiences)
    {
        _dismissals = dismissals;
    }

    internal UserDeletedRecorder(AudienceStore audiences)
    {
        _audiences = audiences;
    }

    /// <inheritdoc />
    public Task OnEvent(UserDeletedEventArgs eventArgs)
    {
        _audiences.ForgetUser(eventArgs.Argument.Id);
        _dismissals?.Forget(eventArgs.Argument.Id);
        return Task.CompletedTask;
    }
}
