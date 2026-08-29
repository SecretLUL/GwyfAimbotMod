# GWYF Trajektorien-Vorhersage

BepInEx-6-Plugin (IL2CPP) für *Golf With Your Friends*, das die Flugbahn des Balls
vorausberechnet und den Schussraum nach Hole-in-One-Lösungen durchsucht.

Akademisches Proof-of-Concept: Gegenstand ist die Frage, wie genau sich die
Starrkörpersimulation eines laufenden Unity/PhysX-Spiels von außen reproduzieren lässt.

## Funktionsumfang

- **Live-Trajektorie** – zeichnet die vorhergesagte Bahn für die aktuelle Zielrichtung
  und Zugstärke, während der Schlag aufgezogen wird.
- **Lösungssuche** – durchsucht Winkel- und Stärkeraum nach einer Bahn, die im Loch endet;
  fällt sonst auf den besten Annäherungsschlag zurück.
- **Stärkeanzeige** – blendet die benötigte Zugstärke am Power-Bar ein.
- **Auto-Aim-Assist** – dreht auf gehaltenem `[F]` die Kamera auf die gefundene Lösung.
- **Parameter-Dump** – schreibt auf `[F9]` die gemessenen Physik-Parameter des laufenden
  Spiels ins Log und als JSON (siehe unten).

> Gedacht für Einzelspieler- und private Sitzungen.

## Voraussetzungen

| Komponente | Version |
|---|---|
| Golf With Your Friends | Unity 2021.3.28f1, IL2CPP |
| BepInEx | 6.0.0-be.764 (Unity.IL2CPP, win-x64) |
| .NET SDK | 6.0+ |

BepInEx muss einmal im Spielordner installiert und das Spiel einmal gestartet worden sein –
erst dabei generiert Il2CppInterop die Assemblies unter `BepInEx/interop/`, gegen die
dieses Projekt kompiliert.

## Bauen

```
cp Local.props.example Local.props     # GameDir auf die eigene Installation setzen
dotnet build
```

Der Build legt `GwyfAimbotMod.dll` automatisch in `<GameDir>/BepInEx/plugins/` ab.
Abschalten über `<DeployToGame>false</DeployToGame>` in `Local.props`.

Alternativ ohne `Local.props`:

```
dotnet build -p:GameDir="C:\Pfad\zum\Spiel"
```

## Projektstruktur

```
Directory.Build.props        GameDir + abgeleitete Pfade, Deploy-Schalter
Local.props.example          Vorlage für maschinenspezifische Pfade (nicht im Git)
GwyfAimbotMod/
  Plugin.cs                  BepInEx-Einstiegspunkt, Config, IL2CPP-Typregistrierung
  AimbotBehaviour.cs         Zielerfassung, Suchzustandsautomat, HUD-Overlay
  TrajectorySimulator.cs     Bahnintegration
  PhysicsParameterDump.cs    Auslesen der echten Physik-Parameter (Log + JSON)
```

## Parameter-Dump

Ein Druck auf `[F9]` (bindbar über `BepInEx/config/com.ammar.gwyf.aimbot.cfg`,
Abschnitt `[Dump]`, Schlüssel `DumpKey`) schreibt einmalig den gesamten Physik-Zustand
in das BepInEx-Log und zusätzlich als JSON nach
`BepInEx/gwyf-physics-dump_<Zeitstempel>.json` – also neben `LogOutput.log`.

Erfasst werden:

| Block | Inhalt |
|---|---|
| `time` | `fixedDeltaTime`, `maximumDeltaTime`, `timeScale` |
| `physicsGlobals` | Gravitation, `bounceThreshold`, Contact-Offset, Solver-Iterationen, Depenetration, Sleep-Threshold und die übrigen prozessweiten PhysX-Statics |
| `rigidbody` | Masse, Drag, Trägheitstensor samt Rotation, Solver-Iterationen, `collisionDetectionMode`, `interpolation`, Constraints, Momentanzustand |
| `ballColliders` | alle Collider des Balls mit Typ, Radius, `lossyScale` und `sharedMaterial` |
| `ballMovement` | beide Drag-Sätze, Sand-/Glue-/Umgebungs-Drag, Zustand der Drag-Umschaltung, `m_maxForce` / `minForce` |
| `m_PowerCurve` | alle Keyframes mit Zeit, Wert, Tangenten, Gewichten und Wrap-Modes |
| `groundProbe`, `wallProbe` | Raycast nach unten bzw. in Blickrichtung: Trefferkollider, Normale und dessen `PhysicMaterial` |

Da Materialien pro Bahn unterschiedlich sind, lohnt sich ein Dump je Loch; jeder Druck
legt eine eigene Datei an.

## Stand

Die Bahnberechnung in `TrajectorySimulator.cs` bildet PhysX derzeit von Hand nach
(eigener Integrator + SphereCasts) und weicht nach wenigen Kontakten deutlich vom
Spielverhalten ab. Bekannte Ursachen: kein Drehimpuls, fehlende Drag-Umschaltung eine
Sekunde nach dem Schlag, ignorierte Trigger-Volumes (Booster, Förderbänder, Wasser),
abweichender Zeitschritt. Überarbeitung in Richtung separater `PhysicsScene` mit dem
Solver des Spiels ist geplant.

Der Parameter-Dump ist der erste Schritt dorthin: Er liefert die Messwerte, mit denen
die geratenen Konstanten im Simulator (`MAX_PHYSICS_SPEED`, `CUP_RADIUS`, die
Default-Bounciness- und Reibungswerte) ersetzt werden.
