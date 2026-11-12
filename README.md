# Dokju

Dokju ist eine Windows-Desktopanwendung für die Verwaltung digitaler Einwilligungen zu Gesprächen mit KI-Protokollierung. Teilnehmende entscheiden auf einer mobilen Webseite. Die zuständige Stelle sieht den aktuellen Stand im Desktopfenster und exportiert den Nachweis zur Ablage unter dem Aktenzeichen.

## Starten

1. `Dokju.zip` vollständig entpacken und `Dokju.exe` im obersten Ordner öffnen. Das Windows-x64-Paket bringt Node.js mit und verwendet .NET Framework 4.8. Der Starter lädt beim nächsten Öffnen die zuletzt aktivierte Version. Den entpackten Startordner dafür aufbewahren.
2. **Beispiel anlegen** wählen. Mit **Im Browser öffnen** lässt sich der Ablauf am gleichen PC testen.
3. **Ich stimme zu** wählen und den persönlichen Widerrufslink öffnen oder für später speichern. Der Desktop aktualisiert den Status alle zwei Sekunden.
4. **Zustimmung widerrufen** wählen. Dokju setzt die Freigabe zurück und zeigt den Widerruf bei der betroffenen Person an.
5. **Nachweis exportieren** wählen. Das PDF lässt sich in der Akte ablegen. Beim ZIP-Export bindet Dokju PDF und JSON-Nachweis aneinander. **Nachweis prüfen** erkennt Änderungen an beiden Dateien. Ältere Dokju-Exporte lassen sich weiterhin anhand des JSON-Nachweises prüfen.

Für einen Smartphone-Test vor dem Anzeigen oder Teilen des QR-Codes unter **Verbindung** die private IPv4-Adresse des WLAN-Adapters auswählen. Smartphone und PC müssen sich im selben Netzwerk befinden. Falls nötig, muss die Windows-Firewall eingehenden Zugriff auf den gewählten Port im privaten Netzwerk erlauben. Der Standardport ist 8787. Ausgegebene Links verwenden die Adresse und den Port zum Zeitpunkt der Ausgabe. Das Desktopprogramm muss geöffnet sein, damit sie funktionieren.

**Diese Version ist ein Pilot für fiktive Testdaten.** Die WLAN-Demo verwendet HTTP. Bei einer Zustimmung im echten Prozess wäre die Stelle für einen weiterhin zugänglichen, gleich einfachen Widerruf verantwortlich.

## GitHub-Updates

**Updates** zeigt die installierte Buildnummer, den Commit und verfügbare Versionen. Dokju sucht kurz nach dem Start, alle sechs Stunden und auf Wunsch manuell nach Updates. Nach Klick auf **Installieren und neu starten** lädt Dokju das Paket, prüft es und startet neu. Bei einem Startfehler öffnet der Starter die vorherige Version. Bei einem Netzfehler bleibt der Arbeitsbereich benutzbar.

Der Workflow in `.github/workflows/windows-release.yml` baut und testet Pull Requests sowie Pushes auf `main`. Bei einem erfolgreichen Push veröffentlicht er `Dokju.zip` für den Commit an der Spitze des Pushes. Für einen Push mit mehreren Commits entsteht damit ein Release für den letzten Commit. Reihenfolge und Commit-ID stehen im Release-Tag und im Paket. Der Updater wählt aus den 30 zuletzt gelisteten Releases den höchsten passenden Build und prüft dessen von GitHub gelieferten SHA-256-Wert vor dem Entpacken.

1. Den Inhalt des Ordners `Dokju` als Wurzel eines öffentlichen GitHub-Repositories verwenden, `main` als Standardbranch setzen und GitHub Actions aktivieren.
2. Auf `main` pushen und die Jobs in **Actions** prüfen. Nach der Veröffentlichung lädt der Live-Test das Paket mit demselben Updater wie die Desktop-App, prüft es und startet die heruntergeladene Version. Der Build kennt sein Repository aus den Release-Metadaten.
3. Für das zuvor lokal gebaute Paket lässt sich das Repository einmalig unter **Updates** als `Konto/Repository` eintragen. Ein über GitHub veröffentlichter Build bindet sich an sein Veröffentlichungs-Repository. Die Desktop-App benötigt für öffentliche Releases keine GitHub-Zugangsdaten.

Die Download-Prüfsumme bezieht sich auf das Paket im gewählten GitHub-Repository. Wer dort Releases veröffentlichen darf, bestimmt, welche Programmversionen die App anbietet. Für den Pilotbetrieb sind die Dokju-Programmdateien ohne Authenticode-Zertifikat gebaut.

[GitHub-Dokumentation zu Releases](https://docs.github.com/en/rest/releases) · [Update-Ablauf von Dividendenfluss](https://github.com/tayco00/dividendenfluss#updates)

## Aufbau

| Bereich | Aufgabe |
| --- | --- |
| `desktop/` | Native Windows-Oberfläche, Vorgangssuche, Nachweisexport und Updateprüfung. |
| `backend/` | QR-Portal und Regeln für Einwilligung, Widerruf, Fristen und Nachweis. Die Desktop-App nutzt dafür einen privaten Prozesskanal. |
| `launcher/`, `shared/` | Start der aktiven Version, Prüfsignal beim Versionswechsel und Rückkehr zur vorherigen Version. |
| `%LOCALAPPDATA%\Dokju` | Verschlüsselter Vorgangsspeicher und Windows-geschützter Schlüssel. Neue Programmversionen liegen darunter separat in `App\versions`. Der Startwechsel kopiert oder migriert keine Vorgangsdaten. |

Ein Arbeitsplatz verwendet jeweils einen aktiven Schreiber. Beim späteren Ausbau müssen Änderungen am Speicherformat einen Rückstart mit der vorherigen Programmversion berücksichtigen. Die aktuelle Version verwendet dasselbe Speicherformat wie der erste Pilot.

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

Für einen ersten kommerziellen Pilot bietet sich eine Jahreslizenz pro Organisation an. Die zuständige Stelle wäre Vertragspartner und Kostenträger. KI-Anbieter wie SpeechMind könnten Dokju integrieren oder vermitteln. Eine Zahlungsbereitschaft, Preisstaffel oder verbindliche Vertriebspartnerschaft wäre im Pilot erst zu prüfen.

## Vor dem Einsatz mit echten Daten

- **Rechtsgrundlage und Freiwilligkeit prüfen.** Die zuständige Stelle muss für den konkreten Gesprächsprozess klären, ob Einwilligung in Betracht kommt und wie der gleichwertige Ablauf ohne Aufnahme aussieht. Für besondere Kategorien personenbezogener Daten und gegebenenfalls Minderjährige sind die zusätzlichen Anforderungen zu klären.
- **Widerruf technisch und organisatorisch durchsetzen.** Ein dauerhaft erreichbarer HTTPS-Dienst, Wiederzugang bei verlorenem Link sowie ein Zustell- und Bestätigungsweg zum KI-Anbieter sind für den realen Betrieb zu definieren. Dokju zeigt im Pilot den aktualisierten Status. Eine Produktintegration benötigt eine vereinbarte Schnittstelle.
- **Akte und Nachweis regeln.** Rollen und Zugriff, Identitätszuordnung, verbindliche Fristen, der Umgang mit exportierten Kopien und ein E-Akte-Import sind organisationsabhängig. Die Demo setzt als editierbare Beispiele zwei Tage für Aufnahme und Transkript und 90 Tage für den Nachweis in Dokju.
- **Texte und Barrierefreiheit prüfen.** Die Fassung in einfachem Deutsch dient als Ausgangspunkt für einen Test mit Zielgruppen. Weitere Sprachen, fachlich geprüfte Leichte Sprache und die Abnahme der Barrierefreiheit gehören zum Pilot mit der zuständigen Stelle.

[DSGVO, insbesondere Artikel 5, 7, 9 und 13](https://eur-lex.europa.eu/eli/reg/2016/679/oj/deu) · [EDSA-Leitlinien zur Einwilligung](https://www.edpb.europa.eu/documents/guideline/guidelines-052020-on-consent-under-regulation-2016679_en)

## Build und Tests

In PowerShell im Ordner Dokju ausführen.

```powershell
.\Build.ps1
.\Test.ps1
```

`.\Build.ps1 -Package` baut und paketiert die Anwendung. Für ein Paket mit Node.js `-NodePath` und `-NodeLicense` aus derselben offiziellen Distribution angeben. Beim Build ohne eingebettete Laufzeit muss Node.js 20 oder neuer im Windows-Pfad verfügbar sein. `.\Test.ps1 -Package` prüft zusätzlich das fertig gepackte Archiv.

Der Build verwendet den Windows-eigenen .NET-Framework-Compiler. Die Node-Komponenten brauchen keine npm-Pakete. Die Desktop-App steuert den lokalen Dienst über einen privaten Prozesskanal. Der Dienst stellt ausschließlich das Bürgerportal auf der gewählten IP-Adresse bereit.

Die Anwendungstests prüfen den HTTP-Handler über eine lokale Named Pipe. Der Windows-Test startet die echte Oberfläche und prüft Suche, Statusfilter, QR-Code, Update-Dialog und PDF-Export. Die Updatetests prüfen die Release-Auswahl, einen simulierten GitHub-Download, Prüfsumme, Pfadvalidierung, Commit-Zuordnung, Aktivierung und Rückstart. Zustimmung und Widerruf wurden zusätzlich in Chromium mit mobiler Fenstergröße geprüft. Ein Smartphone-Test im Ziel-WLAN sowie ein Live-Update aus dem eingerichteten GitHub-Repository stehen für die Inbetriebnahme aus.
