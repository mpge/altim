namespace Altim.Core.Models;

/// <summary>
/// How much weight a reading carries. The UI may present a best-effort figure more
/// quietly, but it never presents one as if it were published.
/// </summary>
public enum MetricConfidence
{
    /// <summary>
    /// The provider states this number itself. Altim reports it unchanged.
    /// </summary>
    Documented = 0,

    /// <summary>
    /// Altim derived this number from local artefacts the vendor does not document
    /// and may change without notice.
    /// </summary>
    BestEffort = 1,
}
