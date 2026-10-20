const descriptions: Record<string, { title: string; recovery: string }> = {
  CALENDAR_UNVERIFIED: { title: 'Exchange calendar is unverified', recovery: 'Supply the verified exchange calendar for this year, then prepare a new revision.' },
  CALENDAR_PUBLICATION_UNQUALIFIED: { title: 'Calendar publication time is unproven', recovery: 'Preserve dated official publication or contemporaneous capture evidence for the calendar.' },
  WEIGHTS_PUBLICATION_UNQUALIFIED: { title: 'Bank-weight publication time is unproven', recovery: 'Supply dated publication/capture evidence. An HTTP Last-Modified header alone does not establish historical availability.' },
  SELECTION_EVIDENCE_INCOMPLETE: { title: 'Option-selection evidence is incomplete', recovery: 'Inspect unknown contracts and missing intervals. Refresh coverage gaps can request new candle responses; unavailable source history remains unknown.' },
  WEIGHTS_UNAVAILABLE_OR_INVALID: { title: 'Applicable bank weights are missing or invalid', recovery: 'Supply a complete, sourced weight set covering the decision time, including any effective-date replacement.' },
  WEIGHTS_SNAPSHOT_TOO_OLD: { title: 'Bank-weight snapshot exceeds the configured age limit', recovery: 'Supply a newer point-in-time snapshot and prepare a new revision.' },
  WEIGHTS_SNAPSHOT_IN_FUTURE: { title: 'Bank-weight snapshot is later than the replay date', recovery: 'Use a snapshot that was available for this historical session.' },
  OPTION_UNIVERSE_UNQUALIFIED: { title: 'Historical option catalogue is unqualified', recovery: 'Supply the complete dated catalogue with point-in-time identity evidence.' },
  OPTION_UNIVERSE_UNAVAILABLE: { title: 'Historical option catalogue is unavailable', recovery: 'Repair the dated contract catalogue and provider mappings before preparing a new revision.' },
  REQUIRED_SERIES_INCOMPLETE: { title: 'Required index, futures or bank observations are incomplete', recovery: 'Inspect required-series gaps and request a new candle revision; missing observations cannot be filled forward.' },
  REQUIRED_SESSION_SERIES_INCOMPLETE: { title: 'Required observations do not cover the whole session', recovery: 'Inspect required-series coverage, including the period after the entry window.' },
  CASH_INDEX_UNAVAILABLE: { title: 'Required cash-index definition is unavailable', recovery: 'Supply the dated index definition and its candle source.' },
  FEES_UNAVAILABLE: { title: 'Applicable fee assumptions are unavailable', recovery: 'Supply a sourced fee schedule effective and available at the replay time.' },
  CANDLE_RESEARCH_DISABLED: { title: 'Candle research is disabled', recovery: 'Inspect the frozen configuration and session/catalogue restrictions before starting another research revision.' },
  NO_ENABLED_STRATEGY_DECISIONS: { title: 'No strategy decision times were evaluated', recovery: 'Inspect enabled strategies and their configured session windows.' },
};

export function ReplayEvidenceBlockers({ issues, compact = false }: { issues: string[]; compact?: boolean }) {
  return <ul>{issues.map(code => {
    const description = descriptions[code];
    return <li key={code}><strong>{description?.title ?? code}</strong>{!compact && <><small> · {code}</small><p>{description?.recovery ?? 'Inspect the recorded input evidence and source/configuration requirements.'}</p></>}</li>;
  })}</ul>;
}
