namespace aclearningutil.Models;

/// <summary>
/// Dictionary detail for one word, read from the external <c>words.db</c>
/// (authored by fetch_word.py in the knowledgebuilder-content repo). Returned
/// by GET /api/WordDictionary. The lists are ordered by the DB's <c>seq</c>
/// column; <c>Examples</c> is capped server-side (see the controller).
/// </summary>
public sealed record WordDictionaryDto
{
    /// <summary>
    /// The canonical DB spelling the entry resolved to. Equal to the requested
    /// word except for spelling variants, which resolve through
    /// <c>words.base_word</c> (e.g. request "colour" -> "color").
    /// </summary>
    public required string Word { get; init; }

    /// <summary>Chinese definition lines, one per sense. Never null; empty when the word has none.</summary>
    public List<WordDefinitionDto> Definitions { get; init; } = new();

    /// <summary>Bilingual example sentences. Never null; empty when the word has none.</summary>
    public List<WordExampleDto> Examples { get; init; } = new();
}

/// <summary>One Chinese definition line of a word entry.</summary>
public sealed record WordDefinitionDto
{
    /// <summary>
    /// Part-of-speech label ("n.", "v.", "adj.", ...). Empty string when the
    /// sense carries none - the norm for phrase glosses (~29% of rows).
    /// </summary>
    public required string PartOfSpeech { get; init; }

    /// <summary>The Chinese meaning text.</summary>
    public required string Meaning { get; init; }
}

/// <summary>One bilingual example sentence of a word entry.</summary>
public sealed record WordExampleDto
{
    /// <summary>The English sentence (not an attribution - the DB column name is misleading).</summary>
    public required string Source { get; init; }

    /// <summary>The Chinese translation of <see cref="Source"/>.</summary>
    public required string Translation { get; init; }
}
