using System.Collections.ObjectModel;
using CloudLight.CodexBridge.Infrastructure;
using CloudLight.CodexBridge.Models;

namespace CloudLight.CodexBridge.ViewModels;

public sealed class TimelineEntry : ObservableObject
{
    private string _text = "";
    private string _status = "";
    private bool _isFailure;

    public string Key { get; init; } = "";
    public string TurnId { get; init; } = "";
    public string ItemId { get; init; } = "";
    public string Kind { get; init; } = "status";
    public string Title { get; init; } = "";
    public bool IsExpandable { get; init; }
    public bool IsTemporary { get; init; }
    public bool IsFailure
    {
        get => _isFailure;
        set
        {
            if (SetProperty(ref _isFailure, value)) OnPropertyChanged(nameof(StatusBrush));
        }
    }
    public string StatusBrush => IsFailure ? "#B42318" : IsTemporary ? "#8A5A00" : "#667085";
    public Visibility MessageVisibility => IsExpandable ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ExpandableVisibility => IsExpandable ? Visibility.Visible : Visibility.Collapsed;

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }
}

public sealed class QuestionOptionViewModel : ObservableObject
{
    private bool _isSelected;

    public required InteractionQuestionOption Model { get; init; }
    public required string GroupName { get; init; }
    public string Label => Model.Label;
    public string Value => Model.Value;
    public string Description => Model.Description;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed class QuestionAnswerViewModel : ObservableObject
{
    private string _textAnswer = "";

    public QuestionAnswerViewModel(InteractionQuestion model)
    {
        Model = model;
        Options = new ObservableCollection<QuestionOptionViewModel>(model.Options.Select(option =>
            new QuestionOptionViewModel { Model = option, GroupName = $"question-{model.Id}" }));
        foreach (var option in Options)
        {
            option.PropertyChanged += (_, _) => AnswerChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? AnswerChanged;
    public InteractionQuestion Model { get; }
    public ObservableCollection<QuestionOptionViewModel> Options { get; }
    public string Id => Model.Id;
    public string Header => Model.Header;
    public string Text => Model.Text;
    public bool Required => Model.Required;
    public Visibility SingleChoiceVisibility => Model.Type == "single-choice" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MultipleChoiceVisibility => Model.Type == "multiple-choice" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TextVisibility => Model.Type == "text" ? Visibility.Visible : Visibility.Collapsed;

    public string TextAnswer
    {
        get => _textAnswer;
        set
        {
            if (SetProperty(ref _textAnswer, value)) AnswerChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string[] Answers => Model.Type == "text"
        ? string.IsNullOrWhiteSpace(TextAnswer) ? [] : [TextAnswer.Trim()]
        : Options.Where(option => option.IsSelected).Select(option => option.Value).ToArray();

    public bool IsValid => !Required || Answers.Length > 0;
}

public sealed class PendingInteractionViewModel : ObservableObject
{
    private bool _isResponding;

    public PendingInteractionViewModel(PendingInteraction model)
    {
        Model = model;
        Questions = new ObservableCollection<QuestionAnswerViewModel>(model.Questions.Select(question => new QuestionAnswerViewModel(question)));
        foreach (var question in Questions)
        {
            question.AnswerChanged += (_, _) => OnPropertyChanged(nameof(CanSubmit));
        }
    }

    public PendingInteraction Model { get; }
    public ObservableCollection<QuestionAnswerViewModel> Questions { get; }
    public string Id => Model.Id;
    public string Kind => Model.Kind;
    public string Title => Model.Title;
    public string Description => Model.Description;
    public string Command => Model.Command;
    public string Cwd => Model.Cwd;
    public string ExpiresAt => Model.ExpiresAt;
    public string FileChangesText => string.Join(Environment.NewLine, Model.FileChanges.Select(change => $"{change.Kind}  {change.Path}"));
    public Visibility CommandVisibility => string.IsNullOrWhiteSpace(Command) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FileChangesVisibility => Model.FileChanges.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ApprovalVisibility => Kind == "user-input" ? Visibility.Collapsed : Visibility.Visible;
    public Visibility AllowVisibility => Kind is "command-approval" or "file-change-approval" or "permissions-approval" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UserInputVisibility => Kind == "user-input" ? Visibility.Visible : Visibility.Collapsed;
    public bool CanSubmit => !IsResponding && Questions.All(question => question.IsValid);

    public bool IsResponding
    {
        get => _isResponding;
        set
        {
            if (SetProperty(ref _isResponding, value)) OnPropertyChanged(nameof(CanSubmit));
        }
    }

    public Dictionary<string, string[]> GetAnswers() => Questions.ToDictionary(question => question.Id, question => question.Answers);
}
