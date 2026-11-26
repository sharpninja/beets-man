using BeetsMan.Desktop.Application.Playback;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BeetsMan.Desktop.Presentation;

/// <summary>Identifies a backend and optional local CLI profile.</summary>
public sealed record PlaybackStrategySelection(string StrategyId, Guid? ProfileId);

/// <summary>Owns development of a local CLI profile and review of an engine switch.</summary>
/// <remarks>2026-11-24: Bind FR-CLI-001#ac-profiles and ac-switch while sharing the existing playback session.</remarks>
/// <remarks>2026-11-25: Confirm the proposal through the session owner even when its audio clock advances.</remarks>
public sealed partial class CliPlaybackViewModel(PresentationServices services, Guid ownerId, PlaybackPresentation playback)
    : PresentationViewModel(services, ownerId)
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand), nameof(ValidateProfileCommand))]
    private CliProfileDraft? profile;

    [ObservableProperty] private CliProfileValidation? validation;
    [ObservableProperty] private CliProfileReceipt? saved;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SwitchCommand))]
    private PlaybackStrategyProposal? proposal;

    protected override Guid WriteOwner => playback.SessionId;

    [RelayCommand]
    private Task ChooseEngineAsync(PlaybackStrategySelection selection, CancellationToken token) =>
        ReadAsync(new GetPlaybackStrategyProposal(playback.SessionId, selection.StrategyId, selection.ProfileId), "proposal",
            result => Proposal = result, token, result => result.SessionId == playback.SessionId &&
                result.StrategyId == selection.StrategyId && result.ProfileId == selection.ProfileId);

    [RelayCommand]
    private Task EditProfileAsync(Guid? profileId, CancellationToken token) => ReadAsync(new GetCliProfile(profileId), "profile", result =>
    {
        Profile = result;
        Saved = null;
    }, token, result => profileId is null || result.ProfileId == profileId);

    [RelayCommand(CanExecute = nameof(CanValidate))]
    private Task ValidateProfileAsync(CancellationToken token) => Profile is { } current
        ? ReadAsync(new ValidateCliProfile(current), "validate", result => Validation = result, token,
            result => Profile == current && result.ProfileId == current.ProfileId && result.DraftRevision == current.Revision)
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveProfileAsync(CancellationToken token) => Profile is { } current
        ? WriteAsync(new SaveCliProfile(current, current.Revision), result =>
        {
            if (Profile is { } latest && latest.ProfileId == current.ProfileId)
                Profile = latest with { Revision = result.Revision };
            Saved = result;
        }, token, result => result.ProfileId == current.ProfileId && Profile?.ProfileId == current.ProfileId)
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private Task SwitchAsync(CancellationToken token) => Proposal is { } proposal
        ? WriteAsync(new ChangePlaybackStrategy(playback.SessionId, proposal.ProposalId, proposal.Revision), result =>
        {
            playback.Apply(result);
            if (Proposal == proposal)
                Proposal = null;
        }, token, playback.Accepts)
        : Task.CompletedTask;

    protected override void OnWriteAvailabilityChanged()
    {
        SwitchCommand.NotifyCanExecuteChanged();
        SaveProfileCommand.NotifyCanExecuteChanged();
    }

    partial void OnProfileChanged(CliProfileDraft? value) => Validation = null;

    private bool CanValidate() => IsActive && Profile is not null;
    private bool CanSave() => CanWrite && Profile is not null;
    private bool CanSwitch() => CanWrite && Proposal is { RequiresConfirmation: true };
}
