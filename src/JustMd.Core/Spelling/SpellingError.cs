namespace JustMd.Core.Spelling
{
    /// <summary>
    /// One misspelling in a string, in UTF-16 units. <see cref="Action"/> is the Windows
    /// CORRECTIVE_ACTION value. An engine leaves <see cref="Language"/> empty; <see cref="DocumentSpeller"/> fills it in.
    /// </summary>
    public sealed record SpellingError(int Start, int Length, int Action, string? Replacement)
    {
        public const int ActionNone = 0;
        public const int ActionReplace = 2;

        public string Language { get; init; } = string.Empty;
    }
}
