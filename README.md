# Dokju

Dokju ist eine Windows-Desktopanwendung für die Verwaltung digitaler Einwilligungen zu Gesprächen mit KI-Protokollierung. Teilnehmende entscheiden auf einer mobilen Webseite. Die zuständige Stelle sieht den aktuellen Stand im Desktopfenster und exportiert den Nachweis zur Ablage unter dem Aktenzeichen.

## Starten

1. [Dokju-Setup.exe](https://github.com/tayco00/Dokju/releases/latest/download/Dokju-Setup.exe) öffnen und den Installer abschließen. Dokju erscheint im Startmenü und unter **Einstellungen → Apps → Installierte Apps**. Die Installation gilt für das aktuelle Windows-Konto und bringt Node.js mit. `Dokju.zip` steht als portable Ausgabe zur Verfügung.
2. **Neuer Vorgang** wählen. Beim ersten Vorgang die verantwortliche Stelle, den KI-Anbieter, den Datenschutzkontakt und die geltenden Aufbewahrungsfristen eintragen. Auf Wunsch übernimmt Dokju diese Angaben beim nächsten Vorgang. `Strg+N` öffnet das Formular direkt.
3. **Ich stimme zu** wählen und den persönlichen Widerrufslink öffnen oder für später speichern. Der Desktop aktualisiert den Status alle zwei Sekunden.
4. **Zustimmung widerrufen** wählen. Dokju setzt die Freigabe zurück und zeigt den Widerruf bei der betroffenen Person an.
5. **Nachweis exportieren** wählen. Das PDF lässt sich in der Akte ablegen. Beim ZIP-Export bindet Dokju PDF und JSON-Nachweis aneinander. **Nachweis prüfen** erkennt Änderungen an beiden Dateien. Ältere Dokju-Exporte lassen sich weiterhin anhand des JSON-Nachweises prüfen.

Vor der ersten Entscheidung lässt sich ein Vorgang bearbeiten. Geänderte Angaben erzeugen neue Einladungen, damit frühere Links keine Entscheidungen auf Grundlage veralteter Informationen zulassen. Eine offene oder abgelaufene Einladung kann einzeln erneuert werden. Hat eine teilnehmende Person anderweitig widerrufen, kann die zuständige Stelle dies bei der ausgewählten Person erfassen. Im Nachweis steht dann **Am Arbeitsplatz erfasst**. Bei eingerichtetem Mobilzugang kann die Stelle den persönlichen Widerrufslink auch erneut bereitstellen.

Ein Wechsel von der portablen Ausgabe zur Installation im selben Windows-Konto übernimmt die vorhandenen Vorgänge. Die Deinstallation entfernt Programm, Verknüpfungen und heruntergeladene Updates. Vorgänge und verschlüsselter Schlüssel bleiben für eine spätere Installation im Windows-Profil erhalten.

## Einwilligungsportal einrichten

**Im Browser öffnen** funktioniert standardmäßig am Arbeitsplatz. Für Mobilgeräte benötigt das Portal eine feste HTTPS-Adresse, ein auf den Geräten vertrauenswürdiges Zertifikat und einen Reverse-Proxy auf dem Dokju-Rechner.

1. Den Proxy so einrichten, dass er nur `/i/` und `/w/` an `127.0.0.1:8787` weiterleitet. Den ursprünglichen `Host`-Header und den Anfragepfad beibehalten. Die URL-Pfade enthalten persönliche Links und gehören weder in Zugriffsprotokolle noch in einen gemeinsam genutzten Cache.
2. Unter **Verbindung** die HTTPS-Adresse ohne Pfad und gegebenenfalls den abweichenden lokalen Port eintragen. Die Adresse muss für die Teilnehmenden erreichbar sein und darf für den QR-Code höchstens 71 Zeichen lang sein.
3. Dieselbe Portaladresse für ausgegebene Links beibehalten. Nach der Umstellung von einer früheren Adresse offene Einladungen erneut teilen und bestehende Widerrufslinks für die betreffende Person bei Bedarf neu bereitstellen.

Der Portaldienst läuft, solange Dokju geöffnet ist. Das aktuelle Release ist für den Betrieb an einem einzelnen Arbeitsplatz ausgelegt. Die Voraussetzungen für den Einsatz in einer Organisation stehen unter **Produktivbetrieb**.

## GitHub-Updates

**Updates** zeigt die installierte Buildnummer, den Commit und verfügbare Versionen. Dokju sucht kurz nach dem Start, alle sechs Stunden und auf Wunsch manuell nach Updates. Nach Klick auf **Installieren und neu starten** lädt Dokju das Paket, prüft es und startet neu. Bei einem Startfehler öffnet der Starter die vorherige Version. Bei einem Netzfehler bleibt der Arbeitsbereich benutzbar.

Der Workflow in `.github/workflows/windows-release.yml` prüft Pull Requests und Pushes auf `main`. Bei einem erfolgreichen Push veröffentlicht er den Installer und das Update-Paket für den letzten Commit des Pushes. Reihenfolge und Commit-ID stehen im Release-Tag und im Paket. Der Updater wählt aus den 30 zuletzt gelisteten Releases den höchsten passenden Build und prüft dessen SHA-256-Wert vor dem Entpacken.

1. Den Inhalt des Ordners `Dokju` als Wurzel eines öffentlichen GitHub-Repositories verwenden, `main` als Standardbranch setzen und GitHub Actions aktivieren.
2. Auf `main` pushen und die Jobs in **Actions** prüfen. Nach der Veröffentlichung lädt und startet die Pipeline das öffentliche Paket mit demselben Updater wie die Desktop-App. Der Build kennt sein Repository aus den Release-Metadaten.
3. Für das zuvor lokal gebaute Paket lässt sich das Repository einmalig unter **Updates** als `Konto/Repository` eintragen. Ein über GitHub veröffentlichter Build bindet sich an sein Veröffentlichungs-Repository. Die Desktop-App benötigt für öffentliche Releases keine GitHub-Zugangsdaten.

Die Download-Prüfsumme bezieht sich auf das Paket im gewählten GitHub-Repository. Wer dort Releases veröffentlichen darf, bestimmt, welche Programmversionen die App anbietet. Die Windows-Programmdateien sind derzeit ohne Authenticode-Zertifikat gebaut.

[GitHub-Dokumentation zu Releases](https://docs.github.com/en/rest/releases) · [Update-Ablauf von Dividendenfluss](https://github.com/tayco00/dividendenfluss#updates)

## Aufbau

| Bereich | Aufgabe |
| --- | --- |
| `desktop/` | Native Windows-Oberfläche, Vorgangssuche, Nachweisexport und Updateprüfung. |
| `backend/` | QR-Portal und Regeln für Einwilligung, Widerruf, Fristen und Nachweis. Die Desktop-App nutzt dafür einen privaten Prozesskanal. |
| `launcher/`, `shared/` | Start der aktiven Version, Prüfsignal beim Versionswechsel und Rückkehr zur vorherigen Version. |
| `%LOCALAPPDATA%\Dokju` | Verschlüsselter Vorgangsspeicher und Windows-geschützter Schlüssel. Neue Programmversionen liegen darunter separat in `App\versions`. Der Startwechsel kopiert oder migriert keine Vorgangsdaten. |

Ein Arbeitsplatz verwendet jeweils einen aktiven Schreiber. Änderungen am Speicherformat müssen einen Rückstart mit der vorherigen Programmversion berücksichtigen.

## Abnahmekriterien

| Kriterium | Verhalten |
| --- | --- |
| Vorgang anlegen und finden | Aktenzeichen, Gesprächsbezeichnung, zuständige Stelle, KI-Anbieter, Kontakt, Aufbewahrungsfristen und 1 bis 12 Teilnehmende. Suche nach Gespräch oder Aktenzeichen und Filter nach Status. |
| QR-Code pro Person | Ein Link pro Person, 30 Minuten gültig. Abgelaufene Links lassen sich einzeln erneuern. Der alte Link wird ungültig. |
| Informierte Entscheidung ohne Konto | Mobile Seite auf Deutsch, in einfachem Deutsch und auf Englisch. Die Seite erläutert Zweck, Speicherung, Kontakt, Rechte und Widerruf. Zustimmung und Ablehnung sind gleichwertig erreichbar. |
| Status des Gesprächs | Freigabe nur bei Zustimmung aller Personen. Ablehnung, Ablauf und Widerruf verhindern die Freigabe. Das Desktopfenster aktualisiert sich automatisch. |
| Widerruf | Persönlicher Link nach der Zustimmung. Ein Klick auf der Widerrufsseite erfasst den Widerruf. Wiederholte Klicks erzeugen keinen weiteren Eintrag. |
| Nachweis für die Akte | PDF und ZIP mit Aktenzeichen, Zeitstempeln, Sprachfassungen, Einwilligungstext und Verlauf. Der ZIP-Nachweis verknüpft PDF und JSON mit installationsgebundenen Prüfwerten. |
| Speicherung und Löschung | AES-256-GCM für den lokalen Datenspeicher, Schlüsselschutz über Windows DPAPI. Fällige Vorgänge werden beim Start und laufend gelöscht. Exportierte Dateien folgen den Regeln ihres Zielordners. |
| Neutralität | Der KI-Anbieter wird je Vorgang angegeben. Dokju verwaltet Einwilligungen eigenständig. Aufnahme, Transkription und Protokollerstellung liegen beim jeweiligen KI-Anbieter. |

## Geschäftsmodell

Die Folien 20–25 aus dem SpeechMind-Expertengespräch beschreiben einen neutralen Dienst für Einwilligungen per QR-Code. Verwaltung und andere Organisationen erhalten einen nachvollziehbaren Nachweis. Teilnehmende können ohne Konto entscheiden und später widerrufen. SpeechMind wird in den Folien als möglicher erster Kunde mit Zugang zu Kommunen vorgestellt. Wer zahlt und wie abgerechnet wird, bleibt auf Folie 25 offen.

Eine Jahreslizenz pro Organisation ist eine mögliche Abrechnungsform. Die zuständige Stelle wäre Vertragspartner und Kostenträger. KI-Anbieter wie SpeechMind könnten Dokju integrieren oder vermitteln. Zahlungsbereitschaft, Preisstaffel und Vertriebspartnerschaften sind offen.

## Produktivbetrieb

- **Rechtsgrundlage und Einwilligung klären.** Die zuständige Stelle prüft Freiwilligkeit, einen gleichwertigen Ablauf ohne Aufnahme und vollständige Datenschutzinformationen zum konkreten Verfahren. Je nach Einsatz sind besondere Datenkategorien, Minderjährige und die Rollen der beteiligten Anbieter gesondert zu betrachten.
- **Widerruf jederzeit ermöglichen.** Ein durchgängig erreichbares Portal und ein vereinbarter Weg zur Durchsetzung beim KI-Anbieter sind für einen organisationsweiten Einsatz erforderlich. Dokju aktualisiert seinen eigenen Status. Den Stopp der Aufnahme und den weiteren Umgang mit den Daten muss der Prozess beim KI-Anbieter sicherstellen.
- **Nachweis und IT-Betrieb absichern.** Die Stelle legt die Zuordnung zur teilnehmenden Person, Rollen und Zugriff, Fristen, Ablage und den Umgang mit exportierten Kopien fest. Für den laufenden Betrieb braucht sie eine erprobte Sicherung und Wiederherstellung einschließlich des Schlüssels, sowie einen Prozess für Störungen und Sicherheitsupdates.
- **Barrierefreiheit abnehmen.** Die zuständige Stelle prüft Portal und Desktopanwendung gegen die für sie geltenden Anforderungen. Die Sprachfassung in einfachem Deutsch kann dabei mit den Zielgruppen geprüft werden.

[DSGVO, insbesondere Artikel 5, 7, 9 und 13](https://eur-lex.europa.eu/eli/reg/2016/679/oj/deu) · [EDSA-Leitlinien zur Einwilligung](https://www.edpb.europa.eu/documents/guideline/guidelines-052020-on-consent-under-regulation-2016679_en)
