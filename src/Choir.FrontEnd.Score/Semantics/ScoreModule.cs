using Choir.Source;

namespace Choir.FrontEnd.Score.Semantics;

public sealed class ScoreModule(ScoreContext context, IReadOnlyList<ScoreSemaUnit> units, IReadOnlyList<ScoreModule> dependencies)
{
    public ScoreContext Context { get; } = context;
    public IReadOnlyList<ScoreSemaUnit> Units { get; } = units;
    public IReadOnlyList<ScoreModule> Dependencies { get; } = dependencies;
}
