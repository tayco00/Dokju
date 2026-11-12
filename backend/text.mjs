export const languages = ['de', 'einfach', 'en'];
const purpose = {
  de: 'Das Gespräch soll als Ton aufgenommen und per KI in Text umgewandelt werden, damit die zuständige Stelle daraus ein Gesprächsprotokoll erstellen kann.',
  einfach: 'Wir möchten den Ton von diesem Gespräch aufnehmen. Ein Computer macht aus dem Ton einen Text. Die Stelle schreibt damit ein Protokoll.',
  en: 'The conversation will be recorded and turned into text using AI so the responsible organisation can prepare a written record.',
};

export function notices(info) {
  return Object.fromEntries(languages.map(language => {
    const retention = {
      de: `Aufnahme und KI-Transkript: höchstens ${info.audioDays} Tage laut Fallprofil. Einwilligungsnachweis: bis zum angezeigten Löschdatum. Das fertige Protokoll gehört zur Akte und hat eigene Aufbewahrungsregeln.`,
      einfach: `Ton und Text vom Computer sollen höchstens ${info.audioDays} Tage gespeichert werden. Ihre Entscheidung speichern wir bis zum Datum unten. Für das fertige Protokoll gelten die Regeln der Akte.`,
      en: `Recording and AI transcript: up to ${info.audioDays} days under this case profile. The consent record is kept until the date shown below. The final case record has its own retention rules.`,
    };
    const choice = {
      de: 'Die Entscheidung ist freiwillig. Bei Ablehnung wird das Gespräch ohne Aufnahme und KI-Transkription fortgesetzt. Nach einer Zustimmung können Sie diese über Ihren persönlichen Link widerrufen. Die Verarbeitung bis zum Widerruf bleibt davon unberührt.',
      einfach: 'Sie entscheiden selbst. Wenn Sie Nein sagen, geht das Gespräch ohne Aufnahme weiter. Sie können später wieder Nein sagen. Dafür gibt es einen eigenen Link. Was vorher passiert ist, bleibt bestehen.',
      en: 'Your choice is voluntary. If you decline, the conversation continues without recording or AI transcription. You can withdraw your consent using your personal link. Withdrawal does not affect processing before it.',
    };
    return [language, {
      version: '1.0',
      heading: { de: 'Einwilligung zur KI-Protokollierung', einfach: 'Dürfen wir dieses Gespräch aufnehmen?', en: 'Consent for AI-assisted notes' }[language],
      purpose: purpose[language],
      retention: retention[language],
      choice: choice[language],
      rights: {
        de: 'Für Fragen, Auskunft, Berichtigung, Löschung und weitere Datenschutzrechte wenden Sie sich an den angegebenen Kontakt. Sie können sich bei einer Datenschutzaufsichtsbehörde beschweren.',
        einfach: 'Sie können Fragen zu Ihren Daten stellen. Sie können zum Beispiel fragen, welche Daten gespeichert sind. Nutzen Sie den Kontakt unten. Sie können sich auch bei einer Datenschutz-Behörde beschweren.',
        en: 'Use the listed contact to ask about your data or to exercise rights such as access, correction and erasure. You can also complain to a data protection authority.',
      }[language],
      controller: info.controller, provider: info.provider, contact: info.contact,
    }];
  }));
}

export const ui = {
  de: {
    demo: 'Demo · Bitte nur Testdaten verwenden', controller: 'Verantwortliche Stelle', provider: 'KI-Anbieter', contact: 'Datenschutzkontakt',
    storage: 'Was wird gespeichert?', rights: 'Ihre Datenschutzrechte', proofDate: 'Löschdatum des Nachweises', decision: 'Ihre Entscheidung',
    yes: 'Ich stimme zu', no: 'Ich lehne ab', until: 'Link gültig bis', accepted: 'Ihre Zustimmung wurde erfasst.',
    declined: 'Sie haben abgelehnt.', declinedInfo: 'Das Gespräch kann ohne Aufnahme und KI-Transkription stattfinden.',
    withdrawn: 'Ihre Zustimmung wurde widerrufen.', withdrawnInfo: 'Dokju zeigt der zuständigen Stelle den Widerruf an.',
    save: 'Speichern Sie den persönlichen Link, wenn Sie Ihre Zustimmung später widerrufen möchten.',
    open: 'Zum persönlichen Widerrufslink', withdraw: 'Zustimmung widerrufen', withdrawTitle: 'Zustimmung widerrufen',
    withdrawText: 'Mit der Schaltfläche widerrufen Sie Ihre Zustimmung für dieses Gespräch. Der bisherige Verlauf bleibt im Nachweis erhalten.',
    invalid: 'Dieser Link ist ungültig oder nicht mehr verfügbar.', time: 'Zeitpunkt', participant: 'Teilnehmende Person',
    session: 'Sitzung abgelaufen. Bitte öffnen Sie den QR-Link erneut.',
    conflict: 'Für diese Einladung wurde bereits eine andere Entscheidung erfasst.', viewDecision: 'Erfasste Entscheidung ansehen',
  },
  einfach: {
    demo: 'Das ist ein Test. Bitte keine echten Daten verwenden.', controller: 'Diese Stelle ist verantwortlich', provider: 'Diese Firma bietet die KI an', contact: 'Kontakt für Datenschutz',
    storage: 'Wie lange speichern wir die Daten?', rights: 'Ihre Rechte', proofDate: 'An diesem Tag löschen wir die Entscheidung', decision: 'Sie entscheiden',
    yes: 'Ja, ich bin einverstanden', no: 'Nein, ich möchte das nicht', until: 'So lange geht der Link', accepted: 'Wir haben Ihr Ja gespeichert.',
    declined: 'Sie haben Nein gesagt.', declinedInfo: 'Das Gespräch geht ohne Aufnahme weiter.',
    withdrawn: 'Ihr Ja gilt nicht mehr.', withdrawnInfo: 'Die zuständige Stelle sieht das in Dokju.',
    save: 'Speichern Sie diesen Link. Damit können Sie später wieder Nein sagen.',
    open: 'Link zum Nein-Sagen öffnen', withdraw: 'Mein Ja zurücknehmen', withdrawTitle: 'Möchten Sie Ihr Ja zurücknehmen?',
    withdrawText: 'Wenn Sie auf die Schaltfläche tippen, gilt Ihr Ja ab jetzt nicht mehr. Die Stelle sieht, wann Sie es zurückgenommen haben.',
    invalid: 'Dieser Link geht nicht mehr.', time: 'Zeit', participant: 'Person',
    session: 'Bitte öffnen Sie den Link vom QR-Code noch einmal.',
    conflict: 'Sie haben schon anders entschieden.', viewDecision: 'Meine Entscheidung ansehen',
  },
  en: {
    demo: 'Demo · Use test data only', controller: 'Data controller', provider: 'AI provider', contact: 'Privacy contact',
    storage: 'What is kept?', rights: 'Your data protection rights', proofDate: 'Consent record deletion date', decision: 'Your decision',
    yes: 'I consent', no: 'I decline', until: 'Link expires at', accepted: 'Your consent was recorded.',
    declined: 'You declined.', declinedInfo: 'The conversation can continue without recording or AI transcription.',
    withdrawn: 'Your consent was withdrawn.', withdrawnInfo: 'Dokju displays the withdrawal to the responsible organisation.',
    save: 'Save this personal link so you can withdraw your consent later.',
    open: 'Open your withdrawal link', withdraw: 'Withdraw my consent', withdrawTitle: 'Withdraw consent',
    withdrawText: 'Use the button to withdraw consent for this conversation. Previous events remain in the consent record.',
    invalid: 'This link is invalid or no longer available.', time: 'Time', participant: 'Participant',
    session: 'Your session expired. Please reopen the QR link.',
    conflict: 'A different choice has already been recorded for this invitation.', viewDecision: 'View recorded choice',
  },
};
