// ---------------------------------------------------------------
//  Quest Item ESP
//
//  HERKUNFT (13.09.2026): Der urspruengliche Quelltext ging beim
//  Wechsel auf CachyOS verloren. Wiederhergestellt durch
//  DEKOMPILIEREN der laufenden QuestItemESP.dll (2.0.0).
// ---------------------------------------------------------------
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.Interactive;
using EFT.Quests;
using UnityEngine;
namespace QuestItemESP
{
	[BepInPlugin("com.kaan.questitemesp", "Quest Item ESP", "2.1.0")]
	public partial class QuestItemEspPlugin : BaseUnityPlugin
	{
		private enum MarkerKind
		{
			QuestFlagged,
			QuestRequired,
			SpaeterNoetig,
			Zone,
			SchluesselFehlt,
			SchluesselDa,
			// Ab hier die Zielarten, die frueher alle als "Zone" in einen
			// Topf gingen. Jede hat jetzt ihr eigenes Symbol und ihre
			// eigene Farbe - man sieht auf einen Blick, was dort zu tun ist.
			Ablegen,
			Peilsender,
			Leuchtkugel,
			Ausgang,
			Fluchtpunkt,
			// Ein Gegenstand, den eine laufende Aufgabe JETZT im Raid abgelegt
			// haben will - eigene Farbe, weil er sofort gebraucht wird.
			Mitbringen
		}

		private struct Marker
		{
			public float Hoehe;

			public string Label;

			public Vector3 Position;

			public float Distance;

			public MarkerKind Kind;

			// Nur bei Fluchtpunkten und aufgabenbezogenen Ausgaengen belegt:
			// offen, Countdown laeuft oder noch nicht freigeschaltet.
			public Ausgangszustand Zustand;

			// Der unuebersetzte Name des Ausgangs. Darueber findet der
			// Ausgangs-Durchlauf die Marke wieder, die eine Aufgabe schon
			// gesetzt hat.
			public string AusgangsName;
		}

		/// <summary>Ein Zonenziel: wozu die Aufgabe dorthin schickt und wie
		/// die Aufgabe heisst.</summary>
		private struct Zonenziel
		{
			public string Quest;

			public MarkerKind Art;
		}

		public const string PluginGuid = "com.kaan.questitemesp";

		internal static ManualLogSource Log;

		private ConfigEntry<bool> _enabled;

		private ConfigEntry<KeyboardShortcut> _toggleKey;

		private ConfigEntry<float> _maxDistance;

		private ConfigEntry<float> _refreshInterval;

		private ConfigEntry<bool> _showQuestFlagged;

		private ConfigEntry<bool> _showQuestRequired;

		private ConfigEntry<bool> _showZones;

		private ConfigEntry<bool> _showDistance;

		private ConfigEntry<bool> _inBehaeltern;

		private ConfigEntry<bool> _nurBenoetigte;

		private ConfigEntry<bool> _auchSpaetere;

		private ConfigEntry<Color> _colorSpaeter;

		private ConfigEntry<string> _spaeterZusatz;

		private ConfigEntry<bool> _bedarfAnzeigen;

		private ConfigEntry<bool> _schluessel;

		private ConfigEntry<bool> _nurFehlendeSchluessel;

		private ConfigEntry<Color> _colorSchluesselFehlt;

		private ConfigEntry<Color> _colorSchluesselDa;

		private ConfigEntry<int> _maxLabels;

		private ConfigEntry<bool> _objektmitte;

		private ConfigEntry<bool> _showQuestName;

		private ConfigEntry<bool> _showThroughWalls;

		private ConfigEntry<int> _fontSize;

		private ConfigEntry<Color> _colorFlagged;

		private ConfigEntry<Color> _colorRequired;

		private ConfigEntry<Color> _colorZone;

		private ConfigEntry<bool> _debugLog;

		private ConfigEntry<bool> _ausgaenge;

		private ConfigEntry<bool> _zonenOhneGrenze;

		private ConfigEntry<bool> _zonenEinfarbig;

		private ConfigEntry<bool> _nurAusAufgaben;

		private ConfigEntry<Color> _colorAblegen;

		private ConfigEntry<Color> _colorPeilsender;

		private ConfigEntry<Color> _colorLeuchtkugel;

		private ConfigEntry<Color> _colorAusgangOffen;

		private ConfigEntry<Color> _colorAusgangCountdown;

		private ConfigEntry<Color> _colorAusgangGesperrt;

		private ConfigEntry<bool> _symboleZeigen;

		private ConfigEntry<bool> _nurImEinsatzbild;

		private ConfigEntry<bool> _beiOptikAus;

		private ConfigEntry<bool> _mitbringenZeigen;

		private ConfigEntry<Color> _colorMitbringen;

		private PropertyInfo _questControllerProp;

		private bool _reflectionReady;

		private readonly List<Marker> _markers = new List<Marker>(64);

		private readonly Dictionary<string, string> _wantedItems = new Dictionary<string, string>();

		// Vorlage -> je laufender Aufgabe (Kennung) der Rest, den das Spiel
		// fuer die offene Bedingung fuehrt. Daraus - nicht aus der
		// Gesamtmenge - entsteht "noch N" fuer verlangte Gegenstaende.
		private readonly Dictionary<string, Dictionary<string, Checkmarks.Restposten>> _restBedarf =
			new Dictionary<string, Dictionary<string, Checkmarks.Restposten>>(StringComparer.Ordinal);

		private readonly Dictionary<string, Zonenziel> _wantedZones = new Dictionary<string, Zonenziel>();

		// Ausgaenge, die eine Aufgabe verlangt: Name des Ausgangs -> Aufgabe.
		private readonly Dictionary<string, string> _wantedExits =
			new Dictionary<string, string>(StringComparer.Ordinal);

		private bool _ausgaengeGemeldet;

		private TriggerWithId[] _triggerCache = new TriggerWithId[0];

		private float _nextWorldRefresh;

		private float _nextQuestRefresh;

		private float _nextTriggerRefresh;

		// Wie oft die Ausloeser-Suche noch laufen darf. Die Zonen liegen fest
		// im Level - einmal beim Start und einmal zur Sicherheit spaeter
		// (falls ein Teil der Karte nachgeladen wurde) reicht.
		private int _triggerSuchenUebrig = 2;

		// KANDIDATEN: Die Fundliste einer Karte hat auf Streets mit mehr
		// Beute ueber 4000 Eintraege, gesucht sind davon eine Handvoll.
		// Frueher wurde jeder Suchlauf ueber alle gegangen. Jetzt wird die
		// Liste EINMAL komplett durchsucht (Vollsuche), und nur was dabei
		// einen Treffer hatte, landet hier. Die regelmaessigen Laeufe
		// pruefen nur noch diese paar Eintraege nach.
		private readonly List<IKillable> _kandidaten = new List<IKillable>();

		// Alle Eintraege der Fundliste, die schon geprueft wurden. Kommt
		// etwas dazu (neue Leiche, weggeworfener Gegenstand), wird nur das
		// Neue geprueft; verschwindet etwas, fliegt es aus den Kandidaten.
		private HashSet<IKillable> _bekannt = new HashSet<IKillable>();

		private List<IKillable> _lootListeBeiSuche;

		private int _lootAnzahlBeiSuche = -1;

		private string _zielStand = "";

		private bool _vollsucheNoetig = true;

		private float _naechsteVollsuche;

		// Sicherheitsnetz fuer das, was die Fundliste nicht meldet: ein Bot
		// legt etwas in eine Kiste, eine Aufgabe wird im Raid erledigt.
		private const float VollsucheSpaetestens = 60f;

		private GUIStyle _style;

		private Texture2D _dot;

		private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>();

		private void Awake()
		{
			Log = base.Logger;
			_enabled = base.Config.Bind("1. Allgemein", "Aktiviert", defaultValue: true, "Marker anzeigen.");
			_toggleKey = base.Config.Bind("1. Allgemein", "Umschalttaste", new KeyboardShortcut(KeyCode.F8), "Blendet die Marker ein und aus. F9 ist vom PAUSE-Mod belegt.");
			_maxDistance = base.Config.Bind("1. Allgemein", "Maximale Entfernung", 150f, new ConfigDescription("Nur Ziele innerhalb dieser Entfernung (Meter).", new AcceptableValueRange<float>(10f, 1000f)));
			_refreshInterval = base.Config.Bind("1. Allgemein", "Aktualisierungsintervall", 0.5f, new ConfigDescription("Sekunden zwischen zwei Suchlaeufen.", new AcceptableValueRange<float>(0.1f, 5f)));
			_showQuestFlagged = base.Config.Bind("2. Kategorien", "Quest-Items (Flag)", defaultValue: true, "Gegenstaende mit gesetztem Quest-Flag: Ordner, Marker, Festplatten, versteckte Kisten.");
			_showQuestRequired = base.Config.Bind("2. Kategorien", "Von Quests verlangte Items", defaultValue: true, "Normale Gegenstaende, die eine aktive Quest fordert (z. B. Bronzeuhr, Salewa).");
			_showZones = base.Config.Bind("2. Kategorien", "Quest-Zonen", defaultValue: true, "Orte zum Platzieren, Markieren oder Aufsuchen.");
			_showDistance = base.Config.Bind("3. Darstellung", "Entfernung anzeigen", defaultValue: true, "");
			_showQuestName = base.Config.Bind("3. Darstellung", "Questnamen anzeigen", defaultValue: true, "Haengt den Namen der zugehoerigen Quest an.");

			_maxLabels = base.Config.Bind("3. Darstellung", "Hoechstens x Beschriftungen", 12,
				new ConfigDescription(
					"Begrenzt, wie viele Namen gleichzeitig im Bild stehen - die naechsten "
					+ "zuerst. Ohne Begrenzung wird es in Fundgebieten zu einem "
					+ "unlesbaren Klumpen. 0 = keine Begrenzung.",
					new AcceptableValueRange<int>(0, 60)));

			_inBehaeltern = base.Config.Bind("2. Kategorien", "Auch in Behaeltern und Leichen suchen", defaultValue: true,
				"Durchsucht auch Kisten, Jacken, Safes und Leichen. Ohne das werden nur "
				+ "lose herumliegende Gegenstaende gefunden - steckt der Aufgaben-"
				+ "Gegenstand in einer Jacke, bekommst du gar keine Markierung.");
			_nurBenoetigte = base.Config.Bind("2. Kategorien", "Nur noch benoetigte anzeigen", defaultValue: true,
				"Nutzt die Rechnung von AllQuestsCheckmarks - dieselbe, die im Inventar "
				+ "\"Total needed: 15 FiR\" und \"In Stash: 5\" anzeigt. Ist der Bedarf "
				+ "gedeckt, wird der Gegenstand nicht mehr markiert. Ohne den Mod bleibt "
				+ "alles wie bisher.");
			_auchSpaetere = base.Config.Bind("2. Kategorien", "Auch fuer noch nicht angenommene Aufgaben", defaultValue: true,
				"AllQuestsCheckmarks kennt auch den Bedarf spaeterer Aufgaben (im Inventar "
				+ "unter \"Future quests\"). Eingeschaltet werden diese Gegenstaende "
				+ "ebenfalls markiert - man sammelt sie ein, bevor die Aufgabe ansteht.");
			_spaeterZusatz = base.Config.Bind("3. Darstellung", "Zusatz bei spaeteren Aufgaben", "(spaeter)",
				"Wird an die Beschriftung gehaengt, wenn die Aufgabe dazu noch gar nicht\n"
				+ "angenommen ist. Sonst sieht so ein Fund genauso aus wie einer fuer\n"
				+ "eine laufende Aufgabe. Leer lassen schaltet den Zusatz ab - die\n"
				+ "eigene Farbe bleibt dann trotzdem.");
			_colorSpaeter = base.Config.Bind("3. Darstellung", "Farbe spaetere Aufgabe", new Color(0.6f, 0.62f, 0.78f, 1f),
				"Gegenstand fuer eine Aufgabe, die du noch nicht angenommen hast.\n"
				+ "Bewusst gedaempft: er ist weniger dringend als alles andere.");
			_bedarfAnzeigen = base.Config.Bind("3. Darstellung", "Offene Menge am Etikett", defaultValue: true,
				"Haengt an die Beschriftung, wie viele Stueck noch fehlen, z. B. \"noch 10 FiR\".");
			_objektmitte = base.Config.Bind("3. Darstellung", "Marke auf die Objektmitte", defaultValue: true,
				"An: die Marke sitzt auf der sichtbaren Mitte des Gegenstands, wie bei Sense.\n"
				+ "Aus: sie sitzt auf dem Ursprungspunkt des Modells plus 15 cm - der liegt\n"
				+ "je nach Modell am Boden oder an einer Ecke.");
			WeltmarkenEinstellungenAnlegen();
			_showThroughWalls = base.Config.Bind("3. Darstellung", "Durch Waende anzeigen", defaultValue: true, "Aus: Marker werden verdeckt, wenn Geometrie dazwischen liegt.");
			_fontSize = base.Config.Bind("3. Darstellung", "Schriftgroesse", 14, new ConfigDescription("", new AcceptableValueRange<int>(1, 40)));
			_colorFlagged = base.Config.Bind("3. Darstellung", "Farbe Quest-Items", new Color(0.75f, 0f, 1f, 1f), "");
			_colorRequired = base.Config.Bind("3. Darstellung", "Farbe verlangte Items", new Color(1f, 0.75f, 0f, 1f), "");
			_colorZone = base.Config.Bind("3. Darstellung", "Farbe Zonen", new Color(0f, 0.85f, 1f, 1f), "");
			_schluessel = base.Config.Bind("2. Kategorien", "Schluessel und Schluesselkarten", defaultValue: true,
				"Markiert jeden Schluessel und jede Schluesselkarte im Raid - auch die, "
				+ "die zu keiner Aufgabe gehoeren.");
			_nurFehlendeSchluessel = base.Config.Bind("2. Kategorien", "Nur fehlende Schluessel", defaultValue: false,
				"Eingeschaltet werden Schluessel, die schon im Lager liegen, gar nicht "
				+ "erst angezeigt - statt sie weiss zu markieren.");
			_colorSchluesselFehlt = base.Config.Bind("3. Darstellung", "Farbe Schluessel - fehlt", new Color(0.75f, 0.3f, 1f, 1f),
				"Schluessel, den du noch nicht besitzt.");
			_colorSchluesselDa = base.Config.Bind("3. Darstellung", "Farbe Schluessel - vorhanden", new Color(0.2f, 1f, 0.35f, 1f),
				"Schluessel, von dem schon mindestens einer im Lager liegt.");
			_nurAusAufgaben = base.Config.Bind("2. Kategorien", "Nur Gegenstaende laufender Aufgaben", defaultValue: true,
				"Ein Quest-Gegenstand traegt sein Kennzeichen IMMER - auch wenn die\n"
				+ "Aufgabe dazu erledigt ist oder du sie nie angenommen hast. Solche\n"
				+ "Stuecke liegen zwar in der Welt, lassen sich aber nicht aufheben.\n"
				+ "An: nur markieren, was eine LAUFENDE Aufgabe verlangt (so macht es\n"
				+ "auch Dynamic Maps).\n"
				+ "Aus: jeden gekennzeichneten Gegenstand markieren, wie frueher.");
			_zonenOhneGrenze = base.Config.Bind("2. Kategorien", "Zonen ohne Entfernungsgrenze", defaultValue: true,
				"Quest-Zonen werden IMMER angezeigt, egal wie weit weg. Sie sind die\n"
				+ "Wegweiser zum Ziel - eine Zone, die erst auftaucht, wenn man fast\n"
				+ "davorsteht, hilft nicht beim Hinfinden. Gilt nur fuer Zonen;\n"
				+ "Gegenstaende bleiben an 'Maximale Entfernung' gebunden.");
			_zonenEinfarbig = base.Config.Bind("3. Darstellung", "Alle Zonenarten in der Zonenfarbe", defaultValue: true,
				"An: jedes Ortsziel - hinlaufen, ablegen, Peilsender, Leuchtkugel -\n"
				+ "bekommt die Farbe unter 'Farbe Zonen'. Die ZEICHEN unterscheiden\n"
				+ "die Art weiterhin.\n"
				+ "Aus: jede Art hat ihre eigene Farbe.");
			_ausgaenge = base.Config.Bind("2. Kategorien", "Meine Ausgaenge", defaultValue: true,
				"Zeigt die Fluchtpunkte, die DIESER Einsatz dir gibt - dieselbe Liste, "
				+ "die das Spiel auf der Ausgangsanzeige fuehrt.");
			// Die Farben sind die des Mods QuestMarkers, damit die Symbole so
			// aussehen wie dort gedacht.
			_colorAblegen = base.Config.Bind("3. Darstellung", "Farbe Ablegen", new Color(0.525f, 0.725f, 0.416f, 1f),
				"Orte, an denen etwas abgelegt oder markiert werden soll.");
			_colorPeilsender = base.Config.Bind("3. Darstellung", "Farbe Peilsender", new Color(0.714f, 0.6f, 0.878f, 1f), "");
			_colorLeuchtkugel = base.Config.Bind("3. Darstellung", "Farbe Leuchtkugel", new Color(0.878f, 0.639f, 0.361f, 1f), "");
			_mitbringenZeigen = base.Config.Bind("2. Kategorien", "Mitzubringendes hervorheben", defaultValue: true,
				"Will eine laufende Aufgabe einen Gegenstand an einem Ort abgelegt\n"
				+ "haben (Propantank, Marker, Kamera ...), wird er markiert, bis du\n"
				+ "genug davon AM MANN traegst - egal wie viele im Versteck liegen.\n"
				+ "Aus: solche Gegenstaende laufen ueber die normale Bedarfsrechnung,\n"
				+ "die das Versteck mitzaehlt.");
			_colorMitbringen = base.Config.Bind("3. Darstellung", "Farbe Mitbringen", new Color(1f, 0.35f, 0.35f, 1f),
				"Gegenstaende, die du JETZT im Raid fuer eine Aufgabe brauchst.");
			_colorAusgangOffen = base.Config.Bind("3. Darstellung", "Farbe Ausgang - offen", new Color(0.42f, 0.85f, 0.45f, 1f), "");
			_colorAusgangCountdown = base.Config.Bind("3. Darstellung", "Farbe Ausgang - Countdown laeuft", new Color(0.95f, 0.72f, 0.3f, 1f), "");
			_colorAusgangGesperrt = base.Config.Bind("3. Darstellung", "Farbe Ausgang - nicht bestaetigt", new Color(0.65f, 0.67f, 0.7f, 1f),
				"Auch fuer Ausgaenge, deren Verfuegbarkeit ausgewuerfelt wird - "
				+ "die Farbe verraet den Wurf bewusst NICHT.");
			_symboleZeigen = base.Config.Bind("3. Darstellung", "Symbole statt Punkte", defaultValue: true,
				"Ein eigenes Zeichen je Zielart. Braucht einen Shader, der Texturen kann - "
				+ "geht das nicht, bleibt es beim Punkt (steht dann im Log).");
			_nurImEinsatzbild = base.Config.Bind("3. Darstellung", "Nur im Einsatzbild", defaultValue: true,
				"Keine Marken im Inventar, nach dem Tod oder in der Verfolgerperspektive.");
			_beiOptikAus = base.Config.Bind("3. Darstellung", "Beim Zielen durch Optik ausblenden", defaultValue: true,
				"Ein vergroesserndes Visier zeichnet mit einer eigenen Kamera - die Marken "
				+ "stuenden im Linsenbild an der falschen Stelle.");
			_debugLog = base.Config.Bind("9. Debug", "Ausfuehrliches Log", defaultValue: false, "Schreibt erkannte Quests, Items und Zonen ins BepInEx-Log.");
			_fontSize.SettingChanged += delegate
			{
				_style = null;
			};
			// Andere Kategorien oder Grenzen im F12-Menue -> die Kandidaten
			// von eben passen nicht mehr, also neu durchsuchen.
			base.Config.SettingChanged += delegate
			{
				_vollsucheNoetig = true;
			};
			SetupReflection();
			Log.LogInfo("Quest Item ESP v2 geladen. Reflection bereit: " + _reflectionReady);
		}

		private void SetupReflection()
		{
			try
			{
				// Zwei Schreibweisen, und es kommt auf die Reihenfolge an:
				// "EFT.Quests.QuestController" ist eine EXPLIZITE
				// Schnittstellen-Umsetzung, wie sie aeltere EFT-Fassungen
				// hatten. In 4.1.6 heisst die Eigenschaft schlicht
				// "QuestController" - nach dem alten Namen zu suchen lieferte
				// null, und damit blieben verlangte Items UND Zonen stumm
				// leer. NonPublic gehoert dazu: explizite Umsetzungen sind
				// nicht oeffentlich.
				string[] namen = { "QuestController", "EFT.Quests.QuestController" };
				for (int i = 0; i < namen.Length && _questControllerProp == null; i++)
				{
					_questControllerProp = typeof(Player).GetProperty(namen[i],
						BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
					if (_questControllerProp != null)
					{
						Log.LogInfo("Quest-Controller gefunden als \"" + namen[i] + "\".");
					}
				}
				if (_questControllerProp == null)
				{
					Log.LogWarning("Quest-Controller nicht gefunden - Kategorien 'verlangte Items' und 'Zonen' bleiben leer.");
				}
				_reflectionReady = true;
			}
			catch (Exception ex)
			{
				Log.LogError("Reflection-Aufbau fehlgeschlagen: " + ex);
			}
		}

		private void Update()
		{
			if (_toggleKey.Value.IsDown())
			{
				_enabled.Value = !_enabled.Value;
				if (!_enabled.Value)
				{
					_markers.Clear();
					WeltmarkenAbgleichen();
				}
				Log.LogInfo("ESP " + (_enabled.Value ? "EIN" : "AUS") + " (Taste)");
			}
			if (!_enabled.Value || !_reflectionReady)
			{
				return;
			}
			if (!Singleton<GameWorld>.Instantiated)
			{
				if (_markers.Count > 0)
				{
					_markers.Clear();
					WeltmarkenAbgleichen();
				}
				if (_triggerCache.Length != 0)
				{
					_triggerCache = new TriggerWithId[0];
					_levelZonen.Clear();
					// Im naechsten Einsatz sofort neu einlesen, nicht erst
					// nach bis zu 30 Sekunden.
					_nextTriggerRefresh = 0f;
				}
				_triggerSuchenUebrig = 2;
				if (_lootListeBeiSuche != null)
				{
					KandidatenVergessen();
				}
				return;
			}
			// Tot, im Inventar, in der Verfolgerperspektive oder durch ein
			// Zielfernrohr schauend: nichts zeichnen. Die Marken werden dabei
			// geleert, nicht nur eingefroren - sonst blieben sie stehen.
			if (!MarkenErlaubt())
			{
				if (_markers.Count > 0)
				{
					_markers.Clear();
					WeltmarkenAbgleichen();
				}
				return;
			}

			try
			{
				if (Time.time >= _nextQuestRefresh)
				{
					_nextQuestRefresh = Time.time + 5f;
					RefreshQuestTargets();
					// Hat sich an den gesuchten Dingen etwas geaendert (Aufgabe
					// erledigt, Teilziel geschafft), neu durchsuchen - sonst nicht.
					string stand = ZielStand();
					if (stand != _zielStand)
					{
						_zielStand = stand;
						_vollsucheNoetig = true;
					}
				}
				if (_triggerSuchenUebrig > 0 && Time.time >= _nextTriggerRefresh)
				{
					_triggerSuchenUebrig--;
					_nextTriggerRefresh = Time.time + 30f;
					// Mit "true" auch die ABGESCHALTETEN Ausloeser. Viele
					// Quest-Zonen liegen im Level als inaktives Objekt und
					// werden erst scharf, wenn die Aufgabe laeuft - ohne das
					// Kennzeichen waren sie fuer uns unsichtbar.
					_triggerCache = UnityEngine.Object.FindObjectsOfType<TriggerWithId>(includeInactive: true);
					_levelZonen.Clear();
					for (int z = 0; z < _triggerCache.Length; z++)
					{
						if (_triggerCache[z] != null && !string.IsNullOrEmpty(_triggerCache[z].Id))
						{
							_levelZonen.Add(_triggerCache[z].Id);
						}
					}
					if (_debugLog.Value)
					{
						Log.LogInfo("Trigger im Level: " + _triggerCache.Length);
					}
				}
				if (Time.time >= _nextWorldRefresh)
				{
					_nextWorldRefresh = Time.time + _refreshInterval.Value;
					RefreshMarkers();
					WeltmarkenAbgleichen();
				}
			}
			catch (Exception ex)
			{
				Log.LogError("Fehler in Update: " + ex);
				_markers.Clear();
			}
		}

		private void RefreshQuestTargets()
		{
			_wantedItems.Clear();
			_restBedarf.Clear();
			_wantedZones.Clear();
			_wantedExits.Clear();
			_mitbringen.Clear();
			_uebersprungen = 0;
			if (!_showQuestRequired.Value && !_showZones.Value)
			{
				return;
			}
			Player mainPlayer = Singleton<GameWorld>.Instance.MainPlayer;
			if (mainPlayer == null)
			{
				return;
			}
			object value = QuestVerwaltung(mainPlayer);
			if (value == null)
			{
				if (_debugLog.Value && !_ohneQuestverwaltung)
				{
					_ohneQuestverwaltung = true;
					Log.LogWarning("Der Spieler hat keine Aufgabenverwaltung - als Scav ist das normal.");
				}
				return;
			}
			_ohneQuestverwaltung = false;
			IEnumerable enumerable = FindQuestCollection(value);
			if (enumerable == null)
			{
				if (_debugLog.Value)
				{
					Log.LogWarning("Keine Quest-Sammlung im Controller gefunden.");
				}
				return;
			}
			int num = 0;
			_aufgabenStand.Clear();
			foreach (object item in enumerable)
			{
				if (item == null)
				{
					continue;
				}
				// ZUERST den Stand jeder Aufgabe merken - auch der noch nicht
				// angenommenen und der erledigten. Erst damit laesst sich
				// spaeter sagen, ob ein Gegenstand zu etwas gehoert, das
				// ueberhaupt noch aussteht.
				string kennung = AufgabenKennung(item);
				if (!string.IsNullOrEmpty(kennung))
				{
					object stand = GetMember(item, "QuestStatus");
					if (stand != null)
					{
						try
						{
							_aufgabenStand[kennung] = Convert.ToInt32(stand);
						}
						catch
						{
						}
					}
				}
				if (!IsStarted(item))
				{
					continue;
				}
				num++;
				string questName = GetQuestName(item);
				foreach (object condition in GetConditions(item))
				{
					if (condition == null)
					{
						continue;
					}
					// Erledigte Teilziele ueberspringen. Bisher wurden ALLE
					// Bedingungen einer laufenden Aufgabe uebernommen - der
					// Marker blieb deshalb stehen, nachdem das Teilziel schon
					// abgehakt war. Erst wenn die GANZE Aufgabe fertig war,
					// verschwand er. Dynamic Maps prueft jede Bedingung
					// einzeln, wir jetzt auch.
					if (BedingungErledigt(item, condition))
					{
						_uebersprungen++;
						continue;
					}
					// Teilziele koennen aufeinander aufbauen: Schritt 2 wird
					// im Spiel erst sichtbar, wenn Schritt 1 erledigt ist.
					// Ohne diese Pruefung haben wir Schritt 2 von Anfang an
					// markiert - eine Marke fuer etwas, das noch gar nicht
					// ansteht.
					if (!BedingungSichtbar(item, condition))
					{
						_uebersprungen++;
						continue;
					}
					ReadCondition(item, condition, questName);
				}
			}
			AmMannZaehlen();
			if (_debugLog.Value)
			{
				Log.LogInfo($"Aktive Quests: {num} | gesuchte Items: {_wantedItems.Count} | Zonen: {_wantedZones.Count} | erledigte Teilziele uebersprungen: {_uebersprungen}");
				// Verteilung der Aufgabenstaende - daran laesst sich ablesen,
				// ob das Auftragsbuch auch die noch nicht angenommenen fuehrt.
				var verteilung = new Dictionary<int, int>();
				foreach (KeyValuePair<string, int> a in _aufgabenStand)
				{
					verteilung.TryGetValue(a.Value, out int n);
					verteilung[a.Value] = n + 1;
				}
				string standText = "";
				foreach (KeyValuePair<int, int> v in verteilung)
				{
					standText += (standText.Length > 0 ? ", " : "") + StandName(v.Key) + "=" + v.Value;
				}
				if (standText != _letzterStandText)
				{
					_letzterStandText = standText;
					Log.LogInfo("Auftragsbuch: " + _aufgabenStand.Count + " Aufgaben (" + standText + ")");
				}
				// Welche Aufgabe schickt wohin? Nur bei Aenderung, sonst
				// steht dieselbe Liste alle fuenf Sekunden im Log.
				string zonenListe = "";
				foreach (KeyValuePair<string, Zonenziel> z in _wantedZones)
				{
					if (zonenListe.Length > 0)
					{
						zonenListe += " ; ";
					}
					zonenListe += z.Value.Art + " [" + z.Value.Quest + "]";
				}
				if (zonenListe != _letzteZonenListe)
				{
					_letzteZonenListe = zonenListe;
					Log.LogInfo("Zonen von: " + ((zonenListe.Length == 0) ? "(keine)" : zonenListe));
				}
			}
		}

		private bool _ohneQuestverwaltung;

		private string _letzteZonenListe = "";

		private string _letzterStandText = "";

		private string _letzteZonenMeldung = "";

		private static string StandName(int stand)
		{
			switch (stand)
			{
				case 0: return "gesperrt";
				case 1: return "annehmbar";
				case 2: return "angenommen";
				case 3: return "abgabebereit";
				case 4: return "abgegeben";
				case 5: return "gescheitert";
				default: return "Stand" + stand;
			}
		}

		/// <summary>Holt die Aufgabenverwaltung des Spielers. Der direkte Weg
		/// steht vorn: den prueft der Compiler mit, waehrend ein falscher
		/// Name bei der Suche ueber Zeichenketten erst im Spiel auffaellt -
		/// und dort nur als stumm leere Liste.</summary>
		private object QuestVerwaltung(Player spieler)
		{
			object direkt = spieler.QuestController;
			if (direkt != null)
			{
				return direkt;
			}
			return (_questControllerProp != null) ? _questControllerProp.GetValue(spieler, null) : null;
		}

		private static IEnumerable FindQuestCollection(object controller)
		{
			try
			{
				PropertyInfo property = controller.GetType().GetProperty("Quests", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (property != null && property.GetValue(controller, null) is IEnumerable result)
				{
					return result;
				}
				FieldInfo field = controller.GetType().GetField("QuestBookClass", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (field != null && field.GetValue(controller) is IEnumerable result2)
				{
					return result2;
				}
			}
			catch (Exception ex)
			{
				Log.LogWarning("Direkter Quest-Zugriff fehlgeschlagen: " + ex.Message);
			}
			Type type = FindType("AbstractQuestClass");
			if (type == null)
			{
				return null;
			}
			Type type2 = controller.GetType();
			for (int i = 0; i < 2; i++)
			{
				List<object> list = new List<object>();
				PropertyInfo[] properties = type2.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				foreach (PropertyInfo p in properties)
				{
					object obj = SafeGet(() => p.GetValue(controller, null));
					if (obj != null)
					{
						list.Add(obj);
					}
				}
				FieldInfo[] fields = type2.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				foreach (FieldInfo f in fields)
				{
					object obj2 = SafeGet(() => f.GetValue(controller));
					if (obj2 != null)
					{
						list.Add(obj2);
					}
				}
				foreach (object item in list)
				{
					if (!(item is IEnumerable enumerable) || item is string)
					{
						continue;
					}
					foreach (object item2 in enumerable)
					{
						if (item2 != null)
						{
							if (type.IsInstanceOfType(item2))
							{
								return enumerable;
							}
							break;
						}
					}
				}
				object obj3 = null;
				foreach (object item3 in list)
				{
					if (!(item3 is string) && !(item3 is IEnumerable))
					{
						Type type3 = item3.GetType();
						if (!type3.IsPrimitive && !type3.IsEnum && (type3.Namespace == null || !type3.Namespace.StartsWith("System")))
						{
							obj3 = item3;
							break;
						}
					}
				}
				if (obj3 == null)
				{
					break;
				}
				controller = obj3;
				type2 = obj3.GetType();
			}
			return null;
		}

		private static int _uebersprungen;

		/// <summary>
		/// Wahr, wenn dieses Teilziel bereits abgehakt ist. Die Aufgabe
		/// selbst laeuft dann noch, nur diese eine Bedingung nicht mehr.
		/// </summary>
		private static MethodInfo _istErledigt;

		private static bool _istErledigtGesucht;

		private static bool BedingungErledigt(object quest, object condition)
		{
			try
			{
				// Hauptweg: das Spiel beantwortet die Frage selbst.
				if (!_istErledigtGesucht)
				{
					_istErledigtGesucht = true;
					foreach (MethodInfo m in quest.GetType().GetMethods(
						BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
					{
						ParameterInfo[] ps = m.GetParameters();
						if (m.Name == "IsConditionDone" && ps.Length == 1
							&& m.ReturnType == typeof(bool)
							&& ps[0].ParameterType.IsInstanceOfType(condition))
						{
							_istErledigt = m;
							break;
						}
					}
					Log.LogInfo("Teilziel-Pruefung: IsConditionDone "
						+ ((_istErledigt != null) ? "gefunden" : "NICHT gefunden, nutze CompletedConditions"));
				}
				if (_istErledigt != null)
				{
					return (bool)_istErledigt.Invoke(quest, new object[] { condition });
				}
				// Rueckfallebene: die Liste der abgehakten Bedingungen. Sie
				// haengt an QuestDataClass, nicht an der Aufgabe selbst.
				string id = GetMember(condition, "id") as string;
				if (string.IsNullOrEmpty(id))
				{
					return false;
				}
				object daten = GetMember(quest, "QuestDataClass") ?? quest;
				// Die Sammlung enthaelt MongoID-Werte - ToString() liefert die Kennung.
				if (GetMember(daten, "CompletedConditions") is IEnumerable fertig)
				{
					foreach (object o in fertig)
					{
						if (o != null && o.ToString() == id)
						{
							return true;
						}
					}
				}
			}
			catch
			{
			}
			return false;
		}

		private static MethodInfo _istSichtbar;

		private static bool _istSichtbarGesucht;

		/// <summary>Fragt das Spiel, ob dieses Teilziel schon ansteht.
		/// Findet sich die Methode nicht, gilt alles als sichtbar - dann
		/// verhaelt sich die ESP wie vor dem Einbau.</summary>
		private static bool BedingungSichtbar(object quest, object condition)
		{
			try
			{
				if (!_istSichtbarGesucht)
				{
					_istSichtbarGesucht = true;
					foreach (MethodInfo m in quest.GetType().GetMethods(
						BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
					{
						ParameterInfo[] ps = m.GetParameters();
						if (m.Name == "CheckVisibilityStatus" && ps.Length == 1
							&& m.ReturnType == typeof(bool)
							&& ps[0].ParameterType.IsInstanceOfType(condition))
						{
							_istSichtbar = m;
							break;
						}
					}
					Log.LogInfo("Sichtbarkeits-Pruefung: CheckVisibilityStatus "
						+ ((_istSichtbar != null) ? "gefunden" : "NICHT gefunden - alle Teilziele gelten als sichtbar"));
				}
				if (_istSichtbar == null)
				{
					return true;
				}
				return (bool)_istSichtbar.Invoke(quest, new object[] { condition });
			}
			catch
			{
				return true;
			}
		}

		/// <summary>Stand jeder Aufgabe im Auftragsbuch: Kennung -> Zahlwert
		/// von EQuestStatus. 2 heisst angenommen, 4 erledigt und abgegeben.</summary>
		private readonly Dictionary<string, int> _aufgabenStand =
			new Dictionary<string, int>(StringComparer.Ordinal);

		/// <summary>Die Kennung einer Aufgabe. Sie steht in der Vorlage, nicht
		/// an der Aufgabe selbst.</summary>
		private static string AufgabenKennung(object quest)
		{
			object vorlage = GetMember(quest, "Template");
			object id = (vorlage != null) ? GetMember(vorlage, "Id") : null;
			if (id == null)
			{
				id = GetMember(quest, "Id");
			}
			return (id != null) ? id.ToString() : null;
		}

		/// <summary>Steht diese Aufgabe noch aus? Erledigte und abgegebene
		/// fallen heraus; eine Aufgabe, die das Auftragsbuch gar nicht kennt,
		/// gilt als kuenftig und bleibt drin.</summary>
		private bool AufgabeStehtAus(string kennung)
		{
			if (string.IsNullOrEmpty(kennung))
			{
				return true;
			}
			if (!_aufgabenStand.TryGetValue(kennung, out int stand))
			{
				// Nicht im Auftragsbuch: eine Aufgabe, die erst noch kommt.
				return true;
			}
			// EQuestStatus: 3 = alle Bedingungen erfuellt, 4 = abgegeben,
			// 5 = gescheitert, 7 = als gescheitert vermerkt, 8 = abgelaufen.
			// Alles davon ist fuer uns erledigt.
			return stand != 3 && stand != 4 && stand != 5 && stand != 7 && stand != 8;
		}

		private static bool IsStarted(object quest)
		{
			object member = GetMember(quest, "QuestStatus");
			if (member == null)
			{
				return false;
			}
			return Convert.ToInt32(member) == 2;
		}

		private static string GetQuestName(object quest)
		{
			object member = GetMember(quest, "Template");
			if (member != null)
			{
				string text = GetMember(member, "Name") as string;
				if (!string.IsNullOrEmpty(text))
				{
					return text;
				}
			}
			object member2 = GetMember(quest, "Id");
			if (member2 == null)
			{
				return "Quest";
			}
			return member2.ToString();
		}

		private static IEnumerable GetConditions(object quest)
		{
			// Bisher nur NecessaryConditions. Dynamic Maps nimmt stattdessen
			// quest.Template.Conditions[AvailableForFinish] - dort stehen
			// Bedingungen, die in NecessaryConditions fehlen koennen. Wir
			// nehmen jetzt beides.
			List<object> alle = new List<object>();
			if (GetMember(quest, "NecessaryConditions") is IEnumerable result)
			{
				foreach (object c in result)
				{
					if (c != null)
					{
						alle.Add(c);
					}
				}
			}
			if (alle.Count == 0 && GetMember(quest, "List_0") is IEnumerable result2)
			{
				foreach (object c in result2)
				{
					if (c != null)
					{
						alle.Add(c);
					}
				}
			}
			VorlagenBedingungen(quest, alle);
			return alle;
		}

		/// <summary>
		/// Liest quest.Template.Conditions und haengt alles an, was unter
		/// dem Schluessel AvailableForFinish steht - so macht es Dynamic Maps.
		/// </summary>
		private static void VorlagenBedingungen(object quest, List<object> ziel)
		{
			try
			{
				object vorlage = GetMember(quest, "Template");
				if (vorlage == null || !(GetMember(vorlage, "Conditions") is IEnumerable woerterbuch))
				{
					return;
				}
				foreach (object eintrag in woerterbuch)
				{
					if (eintrag == null)
					{
						continue;
					}
					Type t = eintrag.GetType();
					PropertyInfo pk = t.GetProperty("Key");
					PropertyInfo pv = t.GetProperty("Value");
					if (pk == null || pv == null)
					{
						continue;
					}
					object schluessel = pk.GetValue(eintrag, null);
					if (schluessel == null || schluessel.ToString() != "AvailableForFinish")
					{
						continue;
					}
					if (pv.GetValue(eintrag, null) is IEnumerable liste)
					{
						foreach (object c in liste)
						{
							// NecessaryConditions ist im Spiel nur ein Filter auf genau
							// diese Liste (Quest.Conditions = Template.Conditions) -
							// dieselben Objekte. Ohne diese Pruefung kaeme jede
							// notwendige Bedingung doppelt an: der Restbedarf
							// verdoppelte sich, Mitbringen-Mengen ebenso.
							if (c != null && !ziel.Exists(schon => ReferenceEquals(schon, c)))
							{
								ziel.Add(c);
							}
						}
					}
				}
			}
			catch
			{
			}
		}

		private void ReadCondition(object quest, object condition, string questName, int tiefe = 0)
		{
			string text = condition.GetType().Name;

			// ZUERST typisiert - die Bedingungsklassen von EFT sind NICHT
			// obfuskiert, man darf sie beim Namen nennen. Das bringt zwei
			// Dinge, die der Weg ueber Feldnamen nicht kann: der Compiler
			// merkt eine Umbenennung sofort, und wir erfahren die ART des
			// Ziels - ablegen, Peilsender, Leuchtkugel sind eben nicht
			// dasselbe wie "irgendwo hinlaufen".
			bool erkannt = false;
			if (_showZones.Value && condition is Condition bedingung)
			{
				switch (bedingung)
				{
					case ConditionLeaveItemAtLocation ablegen:
						erkannt = ZoneMerken(ablegen.zoneId, questName, MarkerKind.Ablegen);
						// Den Gegenstand braucht man IM RAID dabei - merken.
						MitbringenMerken(ablegen.target, ablegen.value, questName, ablegen.zoneId);
						break;
					case ConditionPlaceBeacon peilsender:
						erkannt = ZoneMerken(peilsender.zoneId, questName, MarkerKind.Peilsender);
						MitbringenMerken(peilsender.target, peilsender.value, questName, peilsender.zoneId);
						break;
					case ConditionZone zone:
						erkannt = ZoneMerken(zone.zoneId, questName, MarkerKind.Zone);
						break;
					case ConditionLaunchFlare leuchtkugel:
						erkannt = ZoneMerken(leuchtkugel.zoneID, questName, MarkerKind.Leuchtkugel);
						break;
					case ConditionVisitPlace besuch:
						erkannt = ZoneMerken(besuch.target, questName, MarkerKind.Zone);
						break;
					case ConditionInZone inZone:
						if (inZone.zoneIds != null)
						{
							for (int z = 0; z < inZone.zoneIds.Length; z++)
							{
								erkannt |= ZoneMerken(inZone.zoneIds[z], questName, MarkerKind.Zone);
							}
						}
						break;
					case ConditionExitName ausgang:
						if (!string.IsNullOrEmpty(ausgang.exitName))
						{
							_wantedExits[ausgang.exitName] = questName;
							erkannt = true;
						}
						break;
				}
			}

			// Rueckfall ueber Feldnamen - nur fuer Bedingungen, die oben
			// durchgefallen sind. Frueher lief IMMER dieser Weg.
			if (_showZones.Value && !erkannt)
			{
				string text2 = GetMember(condition, "zoneId") as string;
				if (!string.IsNullOrEmpty(text2))
				{
					ZoneMerken(text2, questName, MarkerKind.Zone);
				}
				// ConditionLaunchFlare schreibt das Feld mit grossem D,
				// ConditionInZone fuehrt gleich eine ganze Liste - beides
				// ging uns bisher durch die Lappen.
				string text2b = GetMember(condition, "zoneID") as string;
				if (!string.IsNullOrEmpty(text2b))
				{
					ZoneMerken(text2b, questName, MarkerKind.Zone);
				}
				if (GetMember(condition, "zoneIds") is string[] zonenListe)
				{
					for (int z = 0; z < zonenListe.Length; z++)
					{
						ZoneMerken(zonenListe[z], questName, MarkerKind.Zone);
					}
				}
				if (text == "ConditionVisitPlace")
				{
					string text3 = GetMember(condition, "target") as string;
					ZoneMerken(text3, questName, MarkerKind.Zone);
				}
			}
			// Zaehler-Bedingungen ("toete X in Zone Y", "erkunde und beobachte
			// A, B und C") tragen ihre echten Bedingungen eine Ebene tiefer.
			//
			// Nur die AEUSSERE Bedingung wird auf erledigt/sichtbar geprueft
			// (das passiert schon beim Aufrufer) - die inneren NICHT. So macht
			// es auch Dynamic Maps. Grund: Innere Bedingungen stehen nicht im
			// Sichtbarkeitsbaum der Aufgabe. CheckVisibilityStatus meldet sie
			// als "nicht sichtbar", und am 21.09.2026 flogen dadurch alle
			// Erkundungs-Aufgaben raus (Lieferverzoegerung - Teil 2).
			IEnumerable innen = null;
			if (tiefe < 4)
			{
				// Typisiert wie Dynamic Maps; der Feldname-Weg bleibt als Rueckfall.
				if (condition is ConditionCounterCreator zaehler && zaehler._templateConditions != null)
				{
					innen = zaehler._templateConditions.Conditions;
				}
				else if (GetMember(condition, "TemplateConditions") is object vorlage)
				{
					innen = GetMember(vorlage, "Conditions") as IEnumerable;
				}
			}
			if (innen != null)
			{
				foreach (object c in innen)
				{
					if (c != null && !object.ReferenceEquals(c, condition))
					{
						ReadCondition(quest, c, questName, tiefe + 1);
					}
				}
			}
			if (!_showQuestRequired.Value || (!(text == "ConditionFindItem") && !(text == "ConditionHandoverItem")) || !(GetMember(condition, "target") is string[] array))
			{
				return;
			}
			// "Bringe 5 Gegenstaende der Kategorie Elektronik" - da stehen in
			// target KATEGORIEN, keine Vorlagen. Wer das nicht abfaengt,
			// vergleicht Kategorie-Kennungen mit Vorlagen-Kennungen: es
			// passt nie etwas, und wenn doch, ist es der falsche Fund.
			if (condition is ConditionFindItem suche && suche.TargetIsCategory)
			{
				if (_debugLog.Value)
				{
					Log.LogInfo("'" + questName + "' sucht eine ganze Kategorie - dafuer gibt es keine Marke.");
				}
				return;
			}
			Checkmarks.Restposten posten = RestAusSpiel(quest, condition, text == "ConditionHandoverItem");
			string aufgabe = AufgabenKennung(quest) ?? questName ?? "";
			for (int i = 0; i < array.Length; i++)
			{
				if (!string.IsNullOrEmpty(array[i]))
				{
					_wantedItems[array[i]] = questName;
					RestMerken(array[i], aufgabe, posten);
				}
			}
		}

		/// <summary>Was diese offene Bedingung laut Spiel noch verlangt:
		/// value minus CurrentValue ihres ProgressCheckers. Hat das Spiel fuer
		/// sie keinen Fortschritt angeschlossen, ist der Posten unbekannt.</summary>
		private static Checkmarks.Restposten RestAusSpiel(object quest, object condition, bool abgabe)
		{
			var posten = new Checkmarks.Restposten
			{
				Abgabe = abgabe,
				Fir = condition is ConditionItem gegenstand && gegenstand.onlyFoundInRaid
			};
			try
			{
				if (quest is Quest aufgabe && condition is Condition bedingung
					&& aufgabe.ProgressCheckers != null
					&& aufgabe.ProgressCheckers.TryGetValue(bedingung, out ConditionProgressChecker pruefer)
					&& pruefer != null && pruefer.HasGetter())
				{
					posten.Rest = bedingung.value - pruefer.CurrentValue;
					posten.Bekannt = true;
				}
			}
			catch
			{
				posten.Bekannt = false;
			}
			return posten;
		}

		/// <summary>Ein Posten je Aufgabe und Vorlage. Verlangt eine Aufgabe
		/// denselben Gegenstand zum Finden UND zum Abgeben, zaehlt nur die
		/// Abgabe - beides zu addieren hiesse, dieselben Stuecke zweimal zu
		/// verlangen.</summary>
		private void RestMerken(string vorlage, string aufgabe, Checkmarks.Restposten neu)
		{
			if (!_restBedarf.TryGetValue(vorlage, out Dictionary<string, Checkmarks.Restposten> jeAufgabe))
			{
				jeAufgabe = new Dictionary<string, Checkmarks.Restposten>(StringComparer.Ordinal);
				_restBedarf[vorlage] = jeAufgabe;
			}
			if (!jeAufgabe.TryGetValue(aufgabe, out Checkmarks.Restposten alt))
			{
				jeAufgabe[aufgabe] = neu;
				return;
			}
			Checkmarks.Restposten ergebnis;
			if (alt.Abgabe != neu.Abgabe)
			{
				// Finden + Abgeben: die Abgabe gilt, FiR verlangt, wer von
				// beiden es verlangt.
				ergebnis = alt.Abgabe ? alt : neu;
				ergebnis.Fir = alt.Fir || neu.Fir;
			}
			else
			{
				// Zwei Bedingungen gleicher Art: beide wollen erfuellt sein.
				ergebnis = alt;
				ergebnis.Rest = Math.Max(0.0, alt.Rest) + Math.Max(0.0, neu.Rest);
				ergebnis.Fir = alt.Fir || neu.Fir;
				ergebnis.Bekannt = alt.Bekannt && neu.Bekannt;
			}
			jeAufgabe[aufgabe] = ergebnis;
		}

		/// <summary>Was eine Aufgabe im Raid abgelegt haben will.</summary>
		private struct Mitbringziel
		{
			public string Quest;

			// Je Ablageort (Zonen-ID) die verlangte Menge. Getrennt gehalten,
			// weil die Orte auf verschiedenen Karten liegen koennen - gezaehlt
			// wird nur, was auf der AKTUELLEN Karte abzulegen ist.
			public List<KeyValuePair<string, int>> Orte;
		}

		// Zonen-IDs, die es auf der aktuellen Karte gibt - aus denselben
		// Ausloesern, die auch die Zonen-Marken benutzen.
		private readonly HashSet<string> _levelZonen = new HashSet<string>(StringComparer.Ordinal);

		// Vorlage -> wie viele eine laufende Aufgabe an einem Ort abgelegt haben will.
		private readonly Dictionary<string, Mitbringziel> _mitbringen =
			new Dictionary<string, Mitbringziel>(StringComparer.Ordinal);

		// Vorlage -> wie viele davon der Spieler gerade am Mann traegt.
		private readonly Dictionary<string, int> _amMann =
			new Dictionary<string, int>(StringComparer.Ordinal);

		// Davon im Einsatz gefunden (FiR).
		private readonly Dictionary<string, int> _amMannFir =
			new Dictionary<string, int>(StringComparer.Ordinal);

		// Wahr im Raid: das Spiel hat das Lager beim Raidstart aus dem
		// Profil genommen. FindItem zaehlt dann nur, was man am Mann traegt.
		private bool _lagerUngesehen;

		private void MitbringenMerken(string[] vorlagen, double menge, string questName, string zoneId)
		{
			if (vorlagen == null)
			{
				return;
			}
			int stueck = Math.Max(1, (int)Math.Ceiling(menge));
			for (int i = 0; i < vorlagen.Length; i++)
			{
				if (string.IsNullOrEmpty(vorlagen[i]))
				{
					continue;
				}
				if (!_mitbringen.TryGetValue(vorlagen[i], out Mitbringziel ziel))
				{
					ziel = new Mitbringziel { Quest = questName ?? "", Orte = new List<KeyValuePair<string, int>>() };
				}
				else if (!string.IsNullOrEmpty(questName) && !ziel.Quest.Contains(questName))
				{
					ziel.Quest = ziel.Quest + " / " + questName;
				}
				ziel.Orte.Add(new KeyValuePair<string, int>(zoneId ?? "", stueck));
				_mitbringen[vorlagen[i]] = ziel;
			}
		}

		/// <summary>Wie viele Stueck dieser Gegenstand auf der AKTUELLEN Karte
		/// abgelegt werden sollen. Orte auf anderen Karten zaehlen nicht: den
		/// Propantank fuer Woods muss man auf Customs nicht einsammeln - fuer
		/// den spaeteren Woods-Einsatz reicht der Vorrat im Versteck.
		/// Ist kein Ort bekannt, zaehlt die Menge vorsichtshalber mit.</summary>
		private int MengeHier(Mitbringziel ziel)
		{
			int summe = 0;
			foreach (KeyValuePair<string, int> ort in ziel.Orte)
			{
				if (string.IsNullOrEmpty(ort.Key) || _levelZonen.Contains(ort.Key))
				{
					summe += ort.Value;
				}
			}
			return summe;
		}

		/// <summary>Zaehlt, wie viele der mitzubringenden Gegenstaende der
		/// Spieler gerade am Mann traegt - Weste, Rucksack, Taschen, alles.
		/// Laeuft mit der Aufgabenauswertung alle paar Sekunden, nicht pro
		/// Fund: das Inventar abzulaufen ist zu teuer fuer jedes Bild.</summary>
		private void AmMannZaehlen()
		{
			_amMann.Clear();
			_amMannFir.Clear();
			_lagerUngesehen = false;
			if (_mitbringen.Count == 0 && _restBedarf.Count == 0)
			{
				return;
			}
			try
			{
				Player ich = Singleton<GameWorld>.Instance?.MainPlayer;
				Inventory inventar = ich?.Profile?.Inventory;
				if (inventar?.Equipment == null)
				{
					return;
				}
				_lagerUngesehen = inventar.Stash == null;
				// Ausruestung plus Aufgabengegenstaende im Raid - dieselbe
				// Auswahl, die AllQuestsCheckmarks im Raid als "am Mann" zaehlt.
				foreach (Item stueck in inventar.GetPlayerItems(EPlayerItems.InRaidItems))
				{
					if (stueck == null)
					{
						continue;
					}
					string v = stueck.StringTemplateId;
					if (string.IsNullOrEmpty(v) || (!_mitbringen.ContainsKey(v) && !_restBedarf.ContainsKey(v)))
					{
						continue;
					}
					int menge = Math.Max(1, stueck.StackObjectsCount);
					_amMann.TryGetValue(v, out int n);
					_amMann[v] = n + menge;
					if (stueck.MarkedAsSpawnedInSession)
					{
						_amMannFir.TryGetValue(v, out int f);
						_amMannFir[v] = f + menge;
					}
				}
			}
			catch (Exception ex)
			{
				if (_debugLog.Value)
				{
					Log.LogWarning("Mitbringen: Inventar nicht lesbar - " + ex.Message);
				}
			}
		}

		/// <summary>Merkt sich eine Zone samt Zielart. Liefert false, wenn
		/// nichts zu merken war - daran erkennt der Aufrufer, ob er noch den
		/// Weg ueber die Feldnamen versuchen muss.</summary>
		private bool ZoneMerken(string zoneId, string questName, MarkerKind art)
		{
			if (string.IsNullOrEmpty(zoneId))
			{
				return false;
			}
			// Kennt das Spiel dieselbe Zone aus zwei Aufgaben, gewinnt die
			// genauere Zielart: "hier etwas ablegen" sagt mehr als "hier
			// hinlaufen".
			if (_wantedZones.TryGetValue(zoneId, out Zonenziel schon)
				&& schon.Art != MarkerKind.Zone && art == MarkerKind.Zone)
			{
				return true;
			}
			_wantedZones[zoneId] = new Zonenziel { Quest = questName, Art = art };
			return true;
		}

		/// <summary>Fingerabdruck dessen, was gerade gesucht wird. Aendert er
		/// sich, stimmen die Kandidaten nicht mehr.</summary>
		private string ZielStand()
		{
			var sb = new System.Text.StringBuilder(256);
			foreach (KeyValuePair<string, string> p in _wantedItems)
			{
				sb.Append(p.Key).Append('=').Append(p.Value).Append(';');
			}
			sb.Append('|');
			foreach (KeyValuePair<string, int> p in _aufgabenStand)
			{
				sb.Append(p.Key).Append('=').Append(p.Value).Append(';');
			}
			sb.Append('|');
			// Beim Mitbringen zaehlt, wie viel man dabei hat: legt man etwas
			// ab, werden andere Stuecke in der Welt wieder interessant.
			foreach (KeyValuePair<string, Mitbringziel> p in _mitbringen)
			{
				_amMann.TryGetValue(p.Key, out int dabei);
				sb.Append(p.Key).Append(':').Append(dabei).Append(';');
			}
			sb.Append('|');
			// Ebenso beim Restbedarf: legt man etwas weg, wird ein vorher
			// ausgeblendeter Fund wieder gebraucht.
			foreach (KeyValuePair<string, Dictionary<string, Checkmarks.Restposten>> p in _restBedarf)
			{
				_amMann.TryGetValue(p.Key, out int dabei);
				_amMannFir.TryGetValue(p.Key, out int dabeiFir);
				sb.Append(p.Key).Append(':').Append(dabei).Append('/').Append(dabeiFir);
				foreach (Checkmarks.Restposten r in p.Value.Values)
				{
					sb.Append(',').Append(r.Bekannt ? r.Rest.ToString("0.##") : "?");
				}
				sb.Append(';');
			}
			return sb.ToString();
		}

		private void KandidatenVergessen()
		{
			_kandidaten.Clear();
			_bekannt.Clear();
			_lootListeBeiSuche = null;
			_lootAnzahlBeiSuche = -1;
			_vollsucheNoetig = true;
		}

		/// <summary>Haelt die Kandidatenliste aktuell. Teuer ist nur die
		/// Vollsuche - sie laeuft beim Raid-Start, wenn sich die gesuchten
		/// Dinge aendern, nach einer Einstellungsaenderung und spaetestens
		/// jede Minute. Sonst wird nur Neues geprueft.</summary>
		private void KandidatenPflegen(List<IKillable> lootList)
		{
			if (!ReferenceEquals(lootList, _lootListeBeiSuche) || Time.time >= _naechsteVollsuche)
			{
				_vollsucheNoetig = true;
			}
			if (_vollsucheNoetig)
			{
				Vollsuche(lootList);
				return;
			}
			if (lootList.Count != _lootAnzahlBeiSuche)
			{
				NeueEintraegePruefen(lootList);
			}
		}

		private void Vollsuche(List<IKillable> lootList)
		{
			_vollsucheNoetig = false;
			_lootListeBeiSuche = lootList;
			_lootAnzahlBeiSuche = lootList.Count;
			_naechsteVollsuche = Time.time + VollsucheSpaetestens;
			_kandidaten.Clear();
			_bekannt.Clear();
			for (int i = 0; i < lootList.Count; i++)
			{
				IKillable eintrag = lootList[i];
				if (eintrag == null)
				{
					continue;
				}
				_bekannt.Add(eintrag);
				if (EintragPruefen(eintrag, Vector3.zero, 0f, vollsuche: true, out bool treffer) && treffer)
				{
					_kandidaten.Add(eintrag);
				}
			}
			if (_debugLog.Value)
			{
				Log.LogInfo("Vollsuche: " + lootList.Count + " Eintraege, " + _kandidaten.Count + " Kandidaten");
			}
		}

		/// <summary>Die Fundliste hat sich geaendert: Verschwundenes aus den
		/// Kandidaten nehmen, nur die NEUEN Eintraege pruefen.</summary>
		private void NeueEintraegePruefen(List<IKillable> lootList)
		{
			var jetzt = new HashSet<IKillable>();
			for (int i = 0; i < lootList.Count; i++)
			{
				if (lootList[i] != null)
				{
					jetzt.Add(lootList[i]);
				}
			}
			for (int i = _kandidaten.Count - 1; i >= 0; i--)
			{
				if (!jetzt.Contains(_kandidaten[i]))
				{
					_kandidaten.RemoveAt(i);
				}
			}
			int neu = 0;
			foreach (IKillable eintrag in jetzt)
			{
				if (_bekannt.Contains(eintrag))
				{
					continue;
				}
				neu++;
				if (EintragPruefen(eintrag, Vector3.zero, 0f, vollsuche: true, out bool treffer) && treffer)
				{
					_kandidaten.Add(eintrag);
				}
			}
			_bekannt = jetzt;
			_lootAnzahlBeiSuche = lootList.Count;
			if (_debugLog.Value && neu > 0)
			{
				Log.LogInfo("Fundliste geaendert: " + neu + " neu geprueft, " + _kandidaten.Count + " Kandidaten");
			}
		}

		/// <summary>
		/// Prueft EINEN Eintrag der Fundliste und setzt bei Treffer Marken.
		/// Rueckgabe false = der Eintrag gibt es nicht mehr (aufgehoben,
		/// zerstoert). Bei der Vollsuche wird ohne Entfernung und Sicht
		/// geprueft; die Marken werden gleich wieder entfernt, es zaehlt nur,
		/// OB etwas gefunden wurde (<paramref name="treffer"/>).
		/// </summary>
		private bool EintragPruefen(IKillable eintrag, Vector3 position, float num, bool vollsuche, out bool treffer)
		{
			treffer = false;
			// In der Fundliste stehen drei Arten: lose Gegenstaende
			// (LootItem), Behaelter (LootableContainer) und Leichen
			// (Corpse, erbt von LootItem). Frueher wurde nur die erste
			// geprueft - was in einer Kiste lag, blieb unsichtbar.
			LootItem lootItem = eintrag as LootItem;
			LootableContainer behaelter = ((lootItem == null) ? (eintrag as LootableContainer) : null);
			Transform ort = ((lootItem != null) ? lootItem.transform : ((behaelter != null) ? behaelter.transform : null));
			if (ort == null)
			{
				return false;
			}

			int vorher = _markers.Count;
			Vector3 position2;
			float distance;
			if (vollsuche)
			{
				position2 = ort.position;
				distance = 0f;
			}
			else
			{
				// Sichtmitte statt Ursprungspunkt: sonst sitzt die Marke bei
				// vielen Modellen im Boden oder neben dem Gegenstand.
				position2 = Sichtmitte(ort);
				float sqrMagnitude = (position2 - position).sqrMagnitude;
				if (sqrMagnitude > num)
				{
					return true;
				}
				distance = Mathf.Sqrt(sqrMagnitude);
				if (!_showThroughWalls.Value && IsBlocked(position, position2))
				{
					return true;
				}
			}

			bool istLeiche = lootItem is Corpse;
			Item wurzel = ((lootItem != null) ? lootItem.Item : behaelter?.ItemOwner?.RootItem);
			if (wurzel == null)
			{
				return true;
			}

			// 1) Der Gegenstand selbst (nur bei losen Funden sinnvoll)
			if (lootItem != null && !istLeiche && !FremdesQuestItem(lootItem))
			{
				PruefeUndMerke(wurzel, lootItem.TemplateId, position2, position, distance, null);
			}

			// 2) Der Inhalt - Kiste, Jacke, Safe, Leiche
			if (_inBehaeltern.Value && !(lootItem != null && !istLeiche && !(wurzel is CompoundItem)))
			{
				System.Collections.IEnumerable inhalt = Inhalte(wurzel);
				if (inhalt != null)
				{
					string art = (istLeiche ? "in Leiche" : "in Behaelter");
					foreach (object o in inhalt)
					{
						Item drin = o as Item;
						if (drin == null || (object)drin == (object)wurzel)
						{
							continue;
						}
						PruefeUndMerke(drin, drin.StringTemplateId, position2, position, distance, art);
					}
				}
			}

			treffer = _markers.Count > vorher;
			if (vollsuche && treffer)
			{
				_markers.RemoveRange(vorher, _markers.Count - vorher);
			}
			return true;
		}

		private void RefreshMarkers()
		{
			_markers.Clear();
			// Die Bildschirm-Anzeige baut ihre Texte nur neu, wenn sich die
			// Marken geaendert haben - nicht in jedem Bild.
			_markenStand++;
			GameWorld instance = Singleton<GameWorld>.Instance;
			if (instance == null)
			{
				if (_debugLog.Value)
				{
					Log.LogWarning("RefreshMarkers: GameWorld ist null");
				}
				return;
			}
			Camera gameCamera = GetGameCamera();
			if (gameCamera == null)
			{
				// Nur beim Wechsel melden, nicht zehnmal je Sekunde.
				if (_debugLog.Value && !_ohneKamera)
				{
					_ohneKamera = true;
					Log.LogWarning("RefreshMarkers: keine Kamera gefunden (EFT.CameraControl.CameraManager.Exist="
						+ EFT.CameraControl.CameraManager.Exist + ") - meist waehrend des Ladens.");
				}
				return;
			}
			if (_ohneKamera)
			{
				_ohneKamera = false;
				if (_debugLog.Value)
				{
					Log.LogInfo("Kamera wieder da: " + gameCamera.name);
				}
			}
			Vector3 position = gameCamera.transform.position;
			float value = _maxDistance.Value;
			List<IKillable> lootList = instance.LootList;
			if (lootList != null)
			{
				KandidatenPflegen(lootList);
				float num = value * value;
				// Nur noch die Kandidaten - rueckwaerts, damit Verschwundene
				// gleich herausgenommen werden koennen.
				for (int i = _kandidaten.Count - 1; i >= 0; i--)
				{
					if (!EintragPruefen(_kandidaten[i], position, num, vollsuche: false, out _))
					{
						_kandidaten.RemoveAt(i);
					}
				}
			}
			if (_debugLog.Value)
			{
				int dmZahl = GegenprobeDynamicMaps(instance);
				if (_checkmarksGemeldet != Checkmarks.Zustand)
				{
					_checkmarksGemeldet = Checkmarks.Zustand;
					Log.LogInfo("AllQuestsCheckmarks: " + Checkmarks.Zustand);
				}
				Log.LogInfo($"Marker: {_markers.Count} gesamt (Items) | gesuchte Tpl: {_wantedItems.Count} | Zonen-IDs: {_wantedZones.Count} | Dynamic-Maps-Zaehlung: {dmZahl}");
				string jetzt = "";
				for (int k = 0; k < _markers.Count; k++)
				{
					if (k > 0)
					{
						jetzt += " ; ";
					}
					jetzt += _markers[k].Label + "@" + _markers[k].Distance.ToString("F0") + "m";
				}
				if (jetzt != _letzteListe)
				{
					_letzteListe = jetzt;
					Log.LogInfo("Gefunden: " + ((jetzt.Length == 0) ? "(nichts)" : jetzt));
				}
			}
			if (_showZones.Value && _wantedZones.Count > 0)
			{
				// Zonen sind Wegweiser: eine, die erst auftaucht, wenn man
				// fast davorsteht, hilft beim Hinfinden nicht mehr. Darum
				// duerfen sie die Entfernungsgrenze ueberspringen - die gilt
				// weiter fuer Gegenstaende, wo sie das Bild sauber haelt.
				float zonenGrenze = (_zonenOhneGrenze != null && _zonenOhneGrenze.Value)
					? float.MaxValue
					: value;
				ZonenEintragen(position, zonenGrenze);
			}

			// Ausgaenge zuletzt: der Durchlauf reicht aufgabenbezogenen
			// Ausgangsmarken, die oben entstanden sind, nur noch ihren
			// Zustand nach, statt sie doppelt zu setzen.
			AusgaengeEintragen(instance, position, value);

			GleicheZusammenfassen();
		}

		// Arbeitsliste fuers Zusammenfassen der Zonen. Eine Zone besteht
		// oft aus MEHREREN Ausloeseflaechen mit derselben Kennung - frueher
		// wurde jede davon eine eigene Marke, und ein grosser Bereich war
		// mit einem Dutzend gleicher Beschriftungen zugepflastert.
		private readonly Dictionary<string, Bounds> _zonenFlaechen =
			new Dictionary<string, Bounds>(StringComparer.Ordinal);

		private void ZonenEintragen(Vector3 auge, float hoechstens)
		{
			_zonenFlaechen.Clear();
			for (int j = 0; j < _triggerCache.Length; j++)
			{
				TriggerWithId ausloeser = _triggerCache[j];
				if (ausloeser == null || ausloeser.transform == null
					|| string.IsNullOrEmpty(ausloeser.Id)
					|| !_wantedZones.ContainsKey(ausloeser.Id))
				{
					continue;
				}
				Bounds flaeche = Ausloeseflaeche(ausloeser);
				if (_zonenFlaechen.TryGetValue(ausloeser.Id, out Bounds bekannt))
				{
					bekannt.Encapsulate(flaeche);
					_zonenFlaechen[ausloeser.Id] = bekannt;
				}
				else
				{
					_zonenFlaechen[ausloeser.Id] = flaeche;
				}
			}

			int gesetzt = 0;
			foreach (KeyValuePair<string, Bounds> paar in _zonenFlaechen)
			{
				Zonenziel ziel = _wantedZones[paar.Key];
				Vector3 ort = paar.Value.center;
				float entfernung = Vector3.Distance(auge, ort);
				if (entfernung > hoechstens)
				{
					continue;
				}
				gesetzt++;
				string text2 = ZonenText(ziel.Art);
				if (_showQuestName.Value && !string.IsNullOrEmpty(ziel.Quest))
				{
					text2 = text2 + " [" + ziel.Quest + "]";
				}
				_markers.Add(new Marker
				{
					Label = text2,
					Position = ort + new Vector3(0f, 0.5f, 0f),
					Distance = entfernung,
					Hoehe = ort.y - auge.y,
					Kind = ziel.Art
				});
			}

			if (_debugLog.Value)
			{
				string text = _zonenFlaechen.Count + " von " + _wantedZones.Count
					+ " Zonen-IDs haben einen Ausloeser im Level (" + _triggerCache.Length
					+ " Ausloeser insgesamt), " + gesetzt + " Marken gesetzt";
				if (text != _letzteZonenMeldung)
				{
					_letzteZonenMeldung = text;
					Log.LogInfo("Zonen: " + text);
				}
			}

			// Ausgaenge, die eine Aufgabe verlangt. Sie tragen den Namen des
			// Ausgangs mit, damit der Ausgangs-Durchlauf sie wiederfindet.
			if (_wantedExits.Count == 0)
			{
				return;
			}
			var leitung = Singleton<GameWorld>.Instance?.ExfiltrationController;
			ExfiltrationPoint[] alle = leitung?.ExfiltrationPoints;
			if (alle == null)
			{
				return;
			}
			for (int i = 0; i < alle.Length; i++)
			{
				ExfiltrationPoint punkt = alle[i];
				if (punkt == null || punkt.Settings == null
					|| !_wantedExits.TryGetValue(punkt.Settings.Name, out string quest))
				{
					continue;
				}
				Vector3 ort = AusgangsMitte(punkt);
				float entfernung = Vector3.Distance(auge, ort);
				if (entfernung > hoechstens)
				{
					continue;
				}
				string text = "Ausgang " + AusgangsText(punkt.Settings.Name);
				if (_showQuestName.Value && !string.IsNullOrEmpty(quest))
				{
					text = text + " [" + quest + "]";
				}
				_markers.Add(new Marker
				{
					Label = text,
					Position = ort,
					Distance = entfernung,
					Hoehe = ort.y - auge.y,
					Kind = MarkerKind.Ausgang,
					Zustand = ZustandVon(punkt),
					AusgangsName = punkt.Settings.Name
				});
			}
		}

		/// <summary>Die Flaeche, die ein Ausloeser wirklich abdeckt. Sein
		/// Ursprungspunkt liegt oft in einer Ecke - bei einem grossen
		/// Bereich stuende die Marke sonst am Rand statt in der Mitte.</summary>
		private static Bounds Ausloeseflaeche(Component ausloeser)
		{
			Bounds flaeche = new Bounds(ausloeser.transform.position, Vector3.zero);
			if (!ausloeser.gameObject.activeInHierarchy)
			{
				return flaeche;
			}
			Collider[] koerper = ausloeser.GetComponents<Collider>();
			for (int i = 0; i < koerper.Length; i++)
			{
				if (koerper[i] != null && koerper[i].enabled)
				{
					flaeche.Encapsulate(koerper[i].bounds);
				}
			}
			return flaeche;
		}

		private static string ZonenText(MarkerKind art)
		{
			switch (art)
			{
				case MarkerKind.Ablegen:
					return "Hier ablegen";
				case MarkerKind.Peilsender:
					return "Peilsender setzen";
				case MarkerKind.Leuchtkugel:
					return "Leuchtkugel abfeuern";
				default:
					return "Quest-Zone";
			}
		}

		// Arbeitslisten fuers Buendeln, als Felder gehalten: der Suchlauf
		// geht alle 0,1 s durch, da soll nicht jedes Mal neuer Speicher
		// angefordert werden.
		private readonly List<Marker> _gebuendelt = new List<Marker>(64);

		private readonly List<int> _anzahl = new List<int>(64);

		/// <summary>Fasst Marken mit gleichem Text am gleichen Ort zu einer
		/// zusammen und haengt die Stueckzahl an.
		///
		/// WARUM: Auf einer Leiche liegen Patronen in mehreren Stapeln, und
		/// jeder Stapel ist ein eigener Fund. Ohne das steht siebenmal
		/// dieselbe Zeile untereinander und man liest keine davon.
		///
		/// Der Ortsvergleich ist wichtig: Derselbe Gegenstand an zwei
		/// verschiedenen Ecken der Karte sind zwei Funde und bleiben
		/// getrennt - sonst wuesste man nicht mehr, wohin man laufen
		/// soll.</summary>
		private void GleicheZusammenfassen()
		{
			if (_buendeln == null || !_buendeln.Value || _markers.Count < 2)
			{
				return;
			}

			float radius = Mathf.Max(0.1f, _buendelRadius.Value);
			float radiusQuadrat = radius * radius;

			_gebuendelt.Clear();
			_anzahl.Clear();

			for (int i = 0; i < _markers.Count; i++)
			{
				Marker jetzt = _markers[i];
				bool gefunden = false;

				for (int k = 0; k < _gebuendelt.Count; k++)
				{
					Marker vorhanden = _gebuendelt[k];
					if (vorhanden.Kind != jetzt.Kind || vorhanden.Label != jetzt.Label)
					{
						continue;
					}
					if ((vorhanden.Position - jetzt.Position).sqrMagnitude > radiusQuadrat)
					{
						continue;
					}

					_anzahl[k] = _anzahl[k] + 1;
					// Die naechstgelegene Fundstelle gibt Ort und Entfernung
					// vor - dorthin laeuft man ohnehin zuerst.
					if (jetzt.Distance < vorhanden.Distance)
					{
						vorhanden.Position = jetzt.Position;
						vorhanden.Distance = jetzt.Distance;
						vorhanden.Hoehe = jetzt.Hoehe;
						_gebuendelt[k] = vorhanden;
					}
					gefunden = true;
					break;
				}

				if (!gefunden)
				{
					_gebuendelt.Add(jetzt);
					_anzahl.Add(1);
				}
			}

			_markers.Clear();
			for (int k = 0; k < _gebuendelt.Count; k++)
			{
				Marker m = _gebuendelt[k];
				if (_anzahl[k] > 1)
				{
					m.Label = m.Label + "  " + _anzahl[k] + "x";
				}
				_markers.Add(m);
			}
		}

		// --- Gegenprobe nach Art von Dynamic Maps -------------------------
		// Dynamic Maps liest GameWorld.LootItems.List_0 und nimmt alles, bei
		// dem Item.QuestItem gesetzt ist. Unsere Quelle GameWorld.LootList
		// enthaelt dieselben Objekte und zusaetzlich Behaelter - die Zahlen
		// muessen also zueinander passen. Tun sie es nicht, wissen wir
		// endlich, an welcher Stelle etwas verloren geht.
		private static FieldInfo _lootItemsFeld;

		private static FieldInfo _listeFeld;

		private static bool _gegenprobeGesucht;

		private static string _letzteListe = "";

		private static string _checkmarksGemeldet = "";

		private static bool _ohneKamera;

		private static int GegenprobeDynamicMaps(GameWorld welt)
		{
			try
			{
				if (!_gegenprobeGesucht)
				{
					_gegenprobeGesucht = true;
					_lootItemsFeld = typeof(GameWorld).GetField("LootItems",
						BindingFlags.Instance | BindingFlags.Public);
					object probe = _lootItemsFeld?.GetValue(welt);
					if (probe != null)
					{
						_listeFeld = probe.GetType().GetField("List_0",
							BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
					}
					Log.LogInfo("Gegenprobe: LootItems-Feld " + ((_lootItemsFeld != null) ? "gefunden" : "FEHLT")
						+ ", List_0 " + ((_listeFeld != null) ? "gefunden" : "FEHLT"));
				}
				if (_lootItemsFeld == null || _listeFeld == null)
				{
					return -1;
				}
				object sammlung = _lootItemsFeld.GetValue(welt);
				if (sammlung == null || !(_listeFeld.GetValue(sammlung) is IEnumerable liste))
				{
					return -1;
				}
				int n = 0;
				foreach (object o in liste)
				{
					LootItem li = o as LootItem;
					if (li != null && li.Item != null && li.Item.QuestItem)
					{
						n++;
					}
				}
				return n;
			}
			catch (Exception ex)
			{
				Log.LogWarning("Gegenprobe fehlgeschlagen: " + ex.Message);
				return -1;
			}
		}

		/// <summary>Aufgaben-Gegenstaende werden je Spieler abgelegt. Liegt
		/// einer fuer ein ANDERES Profil da, kann man ihn zwar sehen, aber
		/// nicht aufnehmen - eine Marke dafuer schickt einen quer ueber die
		/// Karte zu einem Gegenstand, der sich nicht anfassen laesst.
		/// Das Spiel beantwortet die Frage selbst.</summary>
		private static bool FremdesQuestItem(LootItem fund)
		{
			try
			{
				if (fund == null || fund.Item == null || !fund.Item.QuestItem)
				{
					return false;
				}
				Player ich = Singleton<GameWorld>.Instance?.MainPlayer;
				if (ich == null || string.IsNullOrEmpty(ich.ProfileId))
				{
					return false;
				}
				return !fund.IsValidForProfile(ich.ProfileId);
			}
			catch
			{
				// Im Zweifel anzeigen - lieber eine Marke zu viel als eine
				// zu wenig.
				return false;
			}
		}

		private static bool IsBlocked(Vector3 from, Vector3 to)
		{
			Vector3 vector = to - from;
			float magnitude = vector.magnitude;
			if (magnitude < 0.01f)
			{
				return false;
			}
			return Physics.Raycast(from, vector.normalized, magnitude - 0.25f, 1);
		}

		// Der Ursprungspunkt eines Modells liegt selten dort, wo man den
		// Gegenstand sieht - bei Kisten und Leichen ist er oft am Boden oder
		// an einer Ecke. Sense nimmt deshalb die Mitte der Zeichenflaechen.
		// Genau das machen wir hier auch.
		//
		// Der Versatz wird LOKAL gespeichert (also relativ zum Objekt), damit
		// er auch stimmt, wenn der Gegenstand sich dreht oder bewegt. Ohne
		// Zwischenspeicher waere das zu teuer: der Suchlauf geht alle 0,5 s
		// ueber jeden Fund in der Naehe.
		private readonly Dictionary<int, Vector3> _mitteVersatz = new Dictionary<int, Vector3>();

		private Vector3 Sichtmitte(Transform ort)
		{
			if (ort == null)
			{
				return Vector3.zero;
			}
			if (!_objektmitte.Value)
			{
				return ort.position;
			}

			int schluessel = ort.GetInstanceID();
			Vector3 lokal;
			if (!_mitteVersatz.TryGetValue(schluessel, out lokal))
			{
				lokal = MitteBerechnen(ort);
				// Karte nicht unbegrenzt wachsen lassen - bei einem Kartenwechsel
				// sind die alten Kennungen ohnehin wertlos.
				if (_mitteVersatz.Count > 4096)
				{
					_mitteVersatz.Clear();
				}
				_mitteVersatz[schluessel] = lokal;
			}
			return ort.TransformPoint(lokal);
		}

		/// <summary>Mitte aller sichtbaren Zeichenflaechen, als lokaler Punkt.
		/// Ohne Zeichenflaechen die Umrisse, sonst der Ursprung plus 15 cm.</summary>
		private static Vector3 MitteBerechnen(Transform ort)
		{
			Bounds umriss = default(Bounds);
			bool erster = true;

			Renderer[] flaechen = ort.GetComponentsInChildren<Renderer>(includeInactive: false);
			for (int i = 0; i < flaechen.Length; i++)
			{
				Renderer r = flaechen[i];
				// Partikel und Lichtscheine wuerden die Mitte verziehen.
				if (r == null || !r.enabled || r is ParticleSystemRenderer)
				{
					continue;
				}
				if (erster)
				{
					umriss = r.bounds;
					erster = false;
				}
				else
				{
					umriss.Encapsulate(r.bounds);
				}
			}

			if (erster)
			{
				// Manche Funde haben keine eigene Zeichenflaeche (z. B. wenn das
				// Modell erst beim Hinsehen geladen wird) - dann die Umrisse.
				Collider[] koerper = ort.GetComponentsInChildren<Collider>(includeInactive: false);
				for (int i = 0; i < koerper.Length; i++)
				{
					Collider c = koerper[i];
					if (c == null || c.isTrigger)
					{
						continue;
					}
					if (erster)
					{
						umriss = c.bounds;
						erster = false;
					}
					else
					{
						umriss.Encapsulate(c.bounds);
					}
				}
			}

			if (erster)
			{
				return ort.InverseTransformPoint(ort.position + new Vector3(0f, 0.15f, 0f));
			}
			return ort.InverseTransformPoint(umriss.center);
		}

		// Die Kamera, die das Bild wirklich zeichnet, heisst in EFT
		// "FPS Camera". Nimmt man eine andere - etwa die Waffenkamera mit
		// eigenem Blickwinkel - stimmt WorldToScreenPoint nicht mehr, und der
		// Fehler waechst, je weiter der Punkt vom Bildmittelpunkt weg ist.
		// Genau so sah der Fehler aus: die Marke rutschte beim Umsehen weg.
		// AmandsSense nimmt Camera.main - das ist dieselbe Kamera.
		private static Camera _kamera;
		private static string _gemeldeteKamera;

		private static Camera GetGameCamera()
		{
			// Unity-Objekte werden beim Kartenwechsel ungueltig; der ==-Vergleich
			// von Unity faengt das ab.
			if (_kamera != null && _kamera.isActiveAndEnabled)
			{
				return _kamera;
			}

			Camera gewaehlt = null;

			// 1. Die namentlich richtige.
			try
			{
				Camera[] alle = Camera.allCameras;
				for (int i = 0; i < alle.Length; i++)
				{
					Camera c = alle[i];
					if (c != null && c.isActiveAndEnabled && c.name == "FPS Camera")
					{
						gewaehlt = c;
						break;
					}
				}
			}
			catch
			{
			}

			// 2. Wie Sense: die als Hauptkamera markierte.
			if (gewaehlt == null)
			{
				Camera haupt = Camera.main;
				if (haupt != null && haupt.isActiveAndEnabled)
				{
					gewaehlt = haupt;
				}
			}

			// 3. Der Kamera-Verwalter des Spiels.
			if (gewaehlt == null)
			{
				try
				{
					if (EFT.CameraControl.CameraManager.Exist)
					{
						Camera c = EFT.CameraControl.CameraManager.Instance?.Camera;
						if (c != null && c.isActiveAndEnabled)
						{
							gewaehlt = c;
						}
					}
				}
				catch
				{
				}
			}

			// 4. Letzte Rueckfallebene: irgendeine eingeschaltete. Beim Laden
			// eines Raids gibt es kurz noch gar keine - daher kam die Meldung
			// "keine Kamera gefunden" hunderte Male waehrend des Ladens.
			if (gewaehlt == null)
			{
				try
				{
					Camera[] alle = Camera.allCameras;
					for (int i = 0; i < alle.Length; i++)
					{
						if (alle[i] != null && alle[i].isActiveAndEnabled)
						{
							gewaehlt = alle[i];
							break;
						}
					}
				}
				catch
				{
				}
			}

			_kamera = gewaehlt;
			if (gewaehlt != null)
			{
				string kennung = gewaehlt.name + " (Blickwinkel " + gewaehlt.fieldOfView.ToString("F1") + ")";
				if (kennung != _gemeldeteKamera)
				{
					_gemeldeteKamera = kennung;
					Log.LogInfo("Zeichne gegen Kamera: " + kennung);
				}
			}
			return gewaehlt;
		}

		// Die Eigenschaften Item.ShortName und Item.Name liefern NICHT den
		// lesbaren Namen, sondern den Uebersetzungsschluessel - im Spielcode
		// steht ausdruecklich
		//     public string ShortName => Template.ShortNameLocalizationKey;
		// Darum stand im Bild "5bc9bc53d4351e00367fbcee ShortName". Aufloesen
		// muss man ihn mit den Erweiterungsmethoden des Spiels:
		//     LocalizedShortName(this Item)   bzw.   LocalizedName(this Item)
		// Die liegen in einer verschleierten Klasse (derzeit GClass2348), deren
		// Name sich zwischen Spielversionen aendert - deshalb per Reflexion
		// gesucht und einmalig gemerkt, statt fest verdrahtet.
		private static MethodInfo _kurzname;

		private static MethodInfo _langname;

		private static bool _aufloeserGesucht;

		private static void AufloeserSuchen(object item)
		{
			if (_aufloeserGesucht)
			{
				return;
			}
			_aufloeserGesucht = true;
			try
			{
				Type itemTyp = item.GetType();
				Type[] typen;
				try
				{
					typen = itemTyp.Assembly.GetTypes();
				}
				catch (ReflectionTypeLoadException ex)
				{
					typen = ex.Types;
				}
				foreach (Type t in typen)
				{
					// Frueher wurde hier zusaetzlich IsSealed verlangt. Genau
					// daran scheiterte es: die gesuchte Klasse ist abstract,
					// aber nicht sealed.
					if (t == null || !t.IsAbstract)
					{
						continue;
					}
					foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.Public))
					{
						ParameterInfo[] ps = m.GetParameters();
						if (ps.Length != 1 || m.ReturnType != typeof(string)
							|| !ps[0].ParameterType.IsAssignableFrom(itemTyp))
						{
							continue;
						}
						if (m.Name == "LocalizedShortName" && _kurzname == null)
						{
							_kurzname = m;
						}
						else if (m.Name == "LocalizedName" && _langname == null)
						{
							_langname = m;
						}
					}
					if (_kurzname != null && _langname != null)
					{
						break;
					}
				}
			}
			catch
			{
			}
		}

		private static string Aufloesen(MethodInfo m, object item)
		{
			if (m == null)
			{
				return null;
			}
			try
			{
				return m.Invoke(null, new object[1] { item }) as string;
			}
			catch
			{
				return null;
			}
		}

		// Zaehlt den Inhalt eines Behaelters auf. Die Hilfsmethode GetAllItems
		// liegt in einer verschleierten Klasse, deren Name sich zwischen
		// Spielversionen aendert - deshalb per Reflexion gesucht und gemerkt.
		private static MethodInfo _alleInhalte;

		private static bool _inhalteGesucht;

		private static void InhaltsAufzaehlerSuchen(object beispiel)
		{
			if (_inhalteGesucht)
			{
				return;
			}
			_inhalteGesucht = true;
			try
			{
				Type itemTyp = beispiel.GetType();
				Type[] typen;
				try
				{
					typen = itemTyp.Assembly.GetTypes();
				}
				catch (ReflectionTypeLoadException ex)
				{
					typen = ex.Types;
				}
				foreach (Type t in typen)
				{
					if (t == null || !t.IsAbstract || !t.IsSealed)
					{
						continue;
					}
					foreach (MethodInfo mi in t.GetMethods(BindingFlags.Static | BindingFlags.Public))
					{
						if (mi.Name != "GetAllItems")
						{
							continue;
						}
						ParameterInfo[] ps = mi.GetParameters();
						if (ps.Length >= 1 && ps[0].ParameterType.IsAssignableFrom(itemTyp)
							&& typeof(System.Collections.IEnumerable).IsAssignableFrom(mi.ReturnType))
						{
							bool restOptional = true;
							for (int k = 1; k < ps.Length; k++)
							{
								if (!ps[k].IsOptional)
								{
									restOptional = false;
									break;
								}
							}
							if (restOptional)
							{
								_alleInhalte = mi;
								break;
							}
						}
					}
					if (_alleInhalte != null)
					{
						break;
					}
				}
			}
			catch
			{
			}
		}

		private static System.Collections.IEnumerable Inhalte(object wurzel)
		{
			if (wurzel == null)
			{
				return null;
			}
			InhaltsAufzaehlerSuchen(wurzel);
			if (_alleInhalte == null)
			{
				return null;
			}
			try
			{
				ParameterInfo[] ps = _alleInhalte.GetParameters();
				object[] args = new object[ps.Length];
				args[0] = wurzel;
				for (int i = 1; i < ps.Length; i++)
				{
					args[i] = ps[i].DefaultValue;
				}
				return _alleInhalte.Invoke(null, args) as System.Collections.IEnumerable;
			}
			catch
			{
				return null;
			}
		}

		/// <summary>
		/// Prueft einen einzelnen Gegenstand und legt bei Treffer eine
		/// Markierung an. <paramref name="art"/> ist null fuer lose Funde,
		/// sonst "in Behaelter" bzw. "in Leiche".
		/// </summary>
		private void PruefeUndMerke(Item item, string vorlage, Vector3 ortWelt, Vector3 auge,
									float entfernung, string art)
		{
			if (item == null)
			{
				return;
			}
			bool questItem;
			try
			{
				questItem = item.QuestItem;
			}
			catch
			{
				return;
			}
			string questName = null;
			bool verlangt = false;
			// Verlangt eine LAUFENDE Aufgabe diesen Gegenstand gerade?
			bool ausAufgabe = _wantedItems.Count > 0 && !string.IsNullOrEmpty(vorlage)
				&& _wantedItems.TryGetValue(vorlage, out questName);
			if (!questItem && ausAufgabe)
			{
				verlangt = true;
			}
			// Das Quest-Flag haengt am Gegenstand, nicht an deinem Auftragsbuch:
			// Ordner, Festplatten und Fracht tragen es IMMER, auch wenn die
			// zugehoerige Aufgabe laengst erledigt ist oder noch gar nicht
			// angenommen wurde. Sie liegen dann zwar in der Welt, sind aber
			// weder aufzuheben noch zu gebrauchen - und genau solche Marken
			// standen im Raum, ohne dass dort etwas war.
			//
			// Dynamic Maps geht den umgekehrten Weg: erst die offenen
			// Bedingungen, daraus die Orte. Mit diesem Schalter machen wir es
			// genauso.
			if (questItem && _nurAusAufgaben != null && _nurAusAufgaben.Value && !ausAufgabe)
			{
				return;
			}
			// Was sagt AllQuestsCheckmarks? Der Mod weiss, wie viele Stueck
			// verlangt werden und wie viele schon im Lager liegen.
			// Zuerst aufgabengenau rechnen: nur was eine noch AUSSTEHENDE
			// Aufgabe verlangt, zaehlt als Bedarf. Gibt die Tabelle die
			// Zuordnung nicht her, bleibt es bei der alten Gesamtrechnung.
			//
			// Verlangt eine LAUFENDE Bedingung den Gegenstand, entscheidet der
			// Fortschritt, den das Spiel dafuer fuehrt (schon abgegeben, schon
			// gefunden, am Mann) - nicht die Gesamtmenge der Tabelle. Die gilt
			// nur noch fuer Gegenstaende spaeterer Aufgaben, fuer die es
			// keine Spieldaten gibt.
			string wofuerGebraucht = "";
			string bedarfsText;
			Checkmarks.Lage lage;
			if (ausAufgabe && _restBedarf.TryGetValue(vorlage, out Dictionary<string, Checkmarks.Restposten> jeAufgabe))
			{
				_amMann.TryGetValue(vorlage, out int dabeiAlle);
				_amMannFir.TryGetValue(vorlage, out int dabeiFir);
				lage = Checkmarks.PruefenRest(vorlage, jeAufgabe.Values, dabeiFir, dabeiAlle - dabeiFir,
					_lagerUngesehen, out bedarfsText);
				if (lage == Checkmarks.Lage.Unbekannt)
				{
					// Ohne Spieldaten lieber anzeigen als faelschlich ausblenden.
					lage = Checkmarks.Lage.NochNoetig;
				}
			}
			else
			{
				lage = Checkmarks.PruefenJeAufgabe(vorlage, AufgabeStehtAus,
					out bedarfsText, out wofuerGebraucht);
				if (lage == Checkmarks.Lage.Unbekannt)
				{
					lage = Checkmarks.Pruefen(vorlage, out bedarfsText);
					wofuerGebraucht = "";
				}
			}
			// Bedarf gedeckt -> nicht mehr markieren. Gilt auch fuer
			// Quest-Flag-Gegenstaende: liegen 15 von 15 im Lager, muss der
			// sechzehnte nicht mehr aufgesammelt werden.
			// MITBRINGEN: Eine laufende Aufgabe will den Gegenstand an einem Ort
			// abgelegt haben (Propantank, Marker ...). Dafuer zaehlt nur, was man
			// IM RAID bei sich traegt - der Vorrat im Versteck hilft hier nicht.
			// Deshalb schlaegt das die Bedarfsrechnung, die das Versteck
			// mitzaehlt und den Gegenstand sonst als "genug da" ausblendet.
			bool mitbringen = false;
			if (!questItem && _mitbringenZeigen.Value && !string.IsNullOrEmpty(vorlage)
				&& _mitbringen.TryGetValue(vorlage, out Mitbringziel mitZiel))
			{
				_amMann.TryGetValue(vorlage, out int dabei);
				int hier = MengeHier(mitZiel);
				if (hier > 0 && dabei < hier)
				{
					mitbringen = true;
					verlangt = true;
					questName = mitZiel.Quest;
					bedarfsText = "noch " + (hier - dabei) + " mitbringen";
				}
			}
			if (_nurBenoetigte.Value && lage == Checkmarks.Lage.Erledigt && !mitbringen)
			{
				return;
			}
			// Gegenstaende spaeterer Aufgaben stehen in keiner laufenden
			// Bedingung - ohne diesen Zweig faenden wir sie nie.
			bool spaeter = false;
			if (!questItem && !verlangt && _auchSpaetere.Value
				&& lage == Checkmarks.Lage.NochNoetig)
			{
				verlangt = true;
				// WICHTIG ZU MERKEN: Die Aufgabe dazu laeuft noch gar nicht.
				// Ohne diesen Merker sieht die Marke genauso aus wie eine
				// fuer eine angenommene Aufgabe - man rennt hin und wundert
				// sich, wozu der Gegenstand gut sein soll.
				spaeter = true;
				// Der Name kommt aus derselben Rechnung, die den Bedarf
				// ermittelt hat - sie hat die offenen Aufgaben ohnehin
				// durchgesehen.
				if (!string.IsNullOrEmpty(wofuerGebraucht))
				{
					questName = wofuerGebraucht;
				}
			}
			// Schluessel und Schluesselkarten: KeycardItemClass erbt von
			// EFT.InventoryLogic.Key, eine Pruefung deckt also beides ab. Sie haengen
			// nicht an Aufgaben, darum ein eigener Zweig.
			if (!questItem && !verlangt && _schluessel.Value && item is EFT.InventoryLogic.Key)
			{
				int habe = Checkmarks.ImLager(vorlage);
				bool besitzt = habe > 0;
				if (besitzt && _nurFehlendeSchluessel.Value)
				{
					return;
				}
				string beschriftung = ItemName(item);
				if (!string.IsNullOrEmpty(art))
				{
					beschriftung = beschriftung + " (" + art + ")";
				}
				if (habe < 0)
				{
					beschriftung += " - Bestand unbekannt";
				}
				else if (besitzt)
				{
					beschriftung += " - hast du (" + habe + ")";
				}
				_markers.Add(new Marker
				{
					Label = beschriftung,
					Position = _objektmitte.Value ? ortWelt : ortWelt + new Vector3(0f, 0.15f, 0f),
					Distance = entfernung,
					Hoehe = ortWelt.y - auge.y,
					Kind = (besitzt ? MarkerKind.SchluesselDa : MarkerKind.SchluesselFehlt)
				});
				return;
			}
			if ((questItem && !_showQuestFlagged.Value) || (verlangt && !_showQuestRequired.Value)
				|| (!questItem && !verlangt))
			{
				return;
			}
			string text = ItemName(item);
			if (_bedarfAnzeigen.Value && !string.IsNullOrEmpty(bedarfsText))
			{
				text = text + " - " + bedarfsText;
			}
			if (!string.IsNullOrEmpty(art))
			{
				text = text + " (" + art + ")";
			}
			if (_showQuestName.Value && !string.IsNullOrEmpty(questName))
			{
				text = text + " [" + questName + "]";
			}
			if (spaeter && !string.IsNullOrEmpty(_spaeterZusatz.Value))
			{
				text = text + " " + _spaeterZusatz.Value;
			}
			_markers.Add(new Marker
			{
				Label = text,
				Position = _objektmitte.Value ? ortWelt : ortWelt + new Vector3(0f, 0.15f, 0f),
				Distance = entfernung,
				Hoehe = ortWelt.y - auge.y,
				Kind = (questItem
					? MarkerKind.QuestFlagged
					: (mitbringen ? MarkerKind.Mitbringen
						: (spaeter ? MarkerKind.SpaeterNoetig : MarkerKind.QuestRequired)))
			});
		}

		/// <summary>Aufgeloester Name eines Gegenstands, mit Rueckfallebenen.</summary>
		private static string ItemName(Item item)
		{
			// DIREKT aufrufen statt suchen. Die Reflexion hat die Klasse mit
			// den Erweiterungsmethoden nie gefunden: sie ist zwar abstract,
			// aber NICHT sealed - und die uebliche Erkennung fuer eine
			// "static class" verlangt beides. Darum stand im Spiel ueberall
			// nur "Quest-Item".
			string n = Direkt(item, kurz: true);
			if (Untauglich(n, item))
			{
				n = Direkt(item, kurz: false);
			}
			if (Untauglich(n, item))
			{
				// Rueckfallebene: doch noch ueber Reflexion versuchen.
				AufloeserSuchen(item);
				n = Aufloesen(_kurzname, item);
				if (string.IsNullOrEmpty(n))
				{
					n = Aufloesen(_langname, item);
				}
			}
			return Untauglich(n, item) ? "Quest-Item" : n;
		}

		private static string Direkt(Item item, bool kurz)
		{
			try
			{
				return kurz ? item.LocalizedShortName() : item.LocalizedName();
			}
			catch
			{
				return null;
			}
		}

		/// <summary>
		/// Wahr, wenn nichts Brauchbares herauskam. Fehlt ein Eintrag in der
		/// Sprachdatei, gibt das Spiel den Schluessel selbst zurueck - also
		/// etwa "5c1d0c5f86f7744bb2683cf0 ShortName".
		/// </summary>
		private static bool Untauglich(string n, Item item)
		{
			if (string.IsNullOrEmpty(n))
			{
				return true;
			}
			if (n.EndsWith(" ShortName") || n.EndsWith(" Name"))
			{
				return true;
			}
			try
			{
				return n == item.TemplateId.ToString();
			}
			catch
			{
				return false;
			}
		}

		private static string GetItemLabel(LootItem loot)
		{
			try
			{
				object item = loot.Item;
				AufloeserSuchen(item);
				string aufgeloest = Aufloesen(_kurzname, item);
				if (string.IsNullOrEmpty(aufgeloest))
				{
					aufgeloest = Aufloesen(_langname, item);
				}
				if (!string.IsNullOrEmpty(aufgeloest))
				{
					return aufgeloest;
				}
				Type type = item.GetType();
				PropertyInfo property = type.GetProperty("ShortName", BindingFlags.Instance | BindingFlags.Public);
				if (property != null)
				{
					string text = property.GetValue(item, null) as string;
					if (!string.IsNullOrEmpty(text))
					{
						return text;
					}
				}
				PropertyInfo property2 = type.GetProperty("Name", BindingFlags.Instance | BindingFlags.Public);
				if (property2 != null)
				{
					string text2 = property2.GetValue(item, null) as string;
					if (!string.IsNullOrEmpty(text2))
					{
						return text2;
					}
				}
			}
			catch
			{
			}
			if (!string.IsNullOrEmpty(loot.Name))
			{
				return loot.Name;
			}
			return "Quest-Item";
		}

		private static Type FindType(string name)
		{
			if (TypeCache.TryGetValue(name, out var value))
			{
				return value;
			}
			Type type = null;
			try
			{
				Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
				foreach (Assembly assembly in assemblies)
				{
					if (assembly.GetName().Name != "Assembly-CSharp")
					{
						continue;
					}
					Type[] types = assembly.GetTypes();
					foreach (Type type2 in types)
					{
						if (type2.Name == name)
						{
							type = type2;
							break;
						}
					}
					break;
				}
			}
			catch (Exception ex)
			{
				Log.LogWarning("Typsuche '" + name + "' fehlgeschlagen: " + ex.Message);
			}
			TypeCache[name] = type;
			return type;
		}

		private static object GetMember(object obj, string name)
		{
			if (obj == null)
			{
				return null;
			}
			Type type = obj.GetType();
			PropertyInfo p = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (p != null)
			{
				return SafeGet(() => p.GetValue(obj, null));
			}
			FieldInfo f = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (f != null)
			{
				return SafeGet(() => f.GetValue(obj));
			}
			return null;
		}

		private static object SafeGet(Func<object> getter)
		{
			try
			{
				return getter();
			}
			catch
			{
				return null;
			}
		}

		private struct Bildmarke
		{
			public Marker Marke;

			// Platz in _markers - darueber finden sich Text und Groesse im
			// Zwischenspeicher wieder.
			public int Index;

			public float X;

			public float Y;
		}

		private readonly List<Bildmarke> _sichtbar = new List<Bildmarke>();

		private readonly List<Rect> _belegt = new List<Rect>();

		// ZWISCHENSPEICHER fuer die Bildschirm-Anzeige. OnGUI laeuft mehrmals
		// je Bild; Texte zusammensetzen und ausmessen kostet Speicher, der
		// sich als Ruckler (Muellsammlung) bemerkbar macht. Die Texte haengen
		// nur an Name, Entfernung und Hoehe - und die aendern sich erst beim
		// naechsten RefreshMarkers. Darum wird je Markenstand und Schriftgroesse
		// genau einmal gebaut.
		private int _markenStand;

		private int _textStand = -1;

		private int _textSchrift = -1;

		private readonly List<string> _anzeigeTexte = new List<string>(64);

		private readonly List<Vector2> _anzeigeGroessen = new List<Vector2>(64);

		private readonly GUIContent _messInhalt = new GUIContent();

		private static readonly NaechsteZuerst NaechsteZuerstVergleich = new NaechsteZuerst();

		/// <summary>Naechste zuerst - als feste Instanz, weil List.Sort mit
		/// einem Lambda bei jedem Aufruf eine Huelle anlegt.</summary>
		private sealed class NaechsteZuerst : IComparer<Bildmarke>
		{
			public int Compare(Bildmarke a, Bildmarke b)
			{
				return a.Marke.Distance.CompareTo(b.Marke.Distance);
			}
		}

		/// <summary>Schriftgroesse in echten Bildschirmpunkten. Die Einstellung
		/// meint die Groesse bei 1080 Zeilen; auf 1800 oder 2160 Zeilen waechst
		/// sie mit, damit die Schrift gleich gross wirkt - und weil OnGUI in
		/// voller Fensteraufloesung zeichnet, bleibt sie dabei scharf.</summary>
		private int BildschirmSchrift()
		{
			return Mathf.Max(1, Mathf.RoundToInt(_fontSize.Value * Screen.height / 1080f));
		}

		/// <summary>Setzt die Beschriftungen aller Marken zusammen und misst
		/// sie aus - einmal je Markenstand und Schriftgroesse.</summary>
		private void AnzeigeTexteBauen(int schrift)
		{
			if (_textStand == _markenStand && _textSchrift == schrift
				&& _anzeigeTexte.Count == _markers.Count)
			{
				return;
			}
			_textStand = _markenStand;
			_textSchrift = schrift;
			_anzeigeTexte.Clear();
			_anzeigeGroessen.Clear();
			for (int i = 0; i < _markers.Count; i++)
			{
				Marker m = _markers[i];
				string text = m.Label;
				if (_showDistance.Value)
				{
					text = text + "  " + Mathf.RoundToInt(m.Distance) + " m";
					// Die Entfernung ist dreidimensional: 4 m koennen ein
					// Stockwerk hoeher oder tiefer heissen. Ohne diesen Hinweis
					// steht man "genau am Ort" und sieht nichts.
					float h = m.Hoehe;
					if (h > 1.5f)
					{
						text = text + " \u2191" + Mathf.RoundToInt(h) + "m";
					}
					else if (h < -1.5f)
					{
						text = text + " \u2193" + Mathf.RoundToInt(0f - h) + "m";
					}
				}
				_messInhalt.text = text;
				Vector2 groesse = _style.CalcSize(_messInhalt);
				_anzeigeTexte.Add(text);
				_anzeigeGroessen.Add(new Vector2(Mathf.Ceil(groesse.x), Mathf.Ceil(groesse.y)));
			}
		}

		private void OnGUI()
		{
			// Im Weltmarken-Betrieb zeichnet die Spiel-Engine, nicht die Oberflaeche.
			if (_weltmarken != null && _weltmarken.Value)
			{
				return;
			}
			if (!_enabled.Value || _markers.Count == 0)
			{
				return;
			}
			// Gezeichnet wird nur im Repaint-Durchlauf; die anderen Durchlaeufe
			// (Layout, Eingaben) haetten nur Arbeit ohne sichtbare Wirkung.
			if (Event.current == null || Event.current.type != EventType.Repaint)
			{
				return;
			}
			Camera gameCamera = GetGameCamera();
			if (gameCamera == null)
			{
				return;
			}
			EnsureStyle();
			int schrift = BildschirmSchrift();
			if (_style.fontSize != schrift)
			{
				_style.fontSize = schrift;
			}
			AnzeigeTexteBauen(schrift);
			// Ein Bildschirmpunkt bei 1080 Zeilen - Umriss, Abstaende und der
			// Punkt wachsen mit der Aufloesung mit.
			float pixel = Mathf.Max(1f, Mathf.Round(Screen.height / 1080f));

			// Naechste zuerst: sie bekommen ihren Platz, weiter entfernte
			// weichen aus. Sonst verdeckt ein 300-m-Fund den direkt vor dir.
			_sichtbar.Clear();
			for (int i = 0; i < _markers.Count; i++)
			{
				Marker m = _markers[i];
				// Ueber den Sichtfeld-Punkt statt ueber Bildschirmpunkte rechnen:
				// WorldToScreenPoint liefert Pixel in der AUFLOESUNG DER KAMERA,
				// GUI zeichnet aber in der Fensteraufloesung. Mit FSR/DLSS oder
				// einer Aufloesungsskalierung unter 100 % rendert die Kamera
				// kleiner als das Fenster - die Marke wanderte dann vom Objekt
				// weg. WorldToViewportPoint liefert 0..1 und ist davon
				// unabhaengig; mal Screen.width/height ist das genau die
				// Umrechnung screen.x * Screen.width / cam.pixelWidth.
				Vector3 sicht = gameCamera.WorldToViewportPoint(m.Position);
				if (sicht.z <= 0f || sicht.x < 0f || sicht.x > 1f || sicht.y < 0f || sicht.y > 1f)
				{
					continue;   // hinter der Kamera oder ausserhalb des Bildes
				}
				_sichtbar.Add(new Bildmarke
				{
					Marke = m,
					Index = i,
					// Auf ganze Bildpunkte runden: halbe Punkte verteilt die
					// Schriftdarstellung auf zwei Spalten - das wirkt weich.
					X = Mathf.Round(sicht.x * (float)Screen.width),
					Y = Mathf.Round((1f - sicht.y) * (float)Screen.height)
				});
			}
			_sichtbar.Sort(NaechsteZuerstVergleich);

			_belegt.Clear();
			int gezeichnet = 0;
			int hoechstens = _maxLabels.Value;
			Color schatten = new Color(0f, 0f, 0f, 0.85f);
			for (int i = 0; i < _sichtbar.Count; i++)
			{
				if (hoechstens > 0 && gezeichnet >= hoechstens)
				{
					break;
				}
				Bildmarke bm = _sichtbar[i];
				string text = _anzeigeTexte[bm.Index];
				Vector2 groesse = _anzeigeGroessen[bm.Index];

				// Der Punkt bleibt immer, wo der Gegenstand wirklich liegt -
				// nur die Schrift weicht aus. Gibt es ein Symbol fuer die
				// Zielart, steht es an derselben Stelle; hier braucht es
				// keinen besonderen Shader, GUI.DrawTexture faerbt ueber
				// GUI.color ein.
				Texture2D zeichen = (_symboleZeigen != null && _symboleZeigen.Value ? SymbolTextur(bm.Marke.Kind) : null);
				Color farbe = ColorFor(bm.Marke);
				if (zeichen != null)
				{
					float kante = Mathf.Round(schrift + 4f * pixel);
					Color vorher = GUI.color;
					GUI.color = farbe;
					GUI.DrawTexture(new Rect(bm.X - Mathf.Round(kante * 0.5f), bm.Y - Mathf.Round(kante * 0.5f), kante, kante), zeichen);
					GUI.color = vorher;
				}
				else
				{
					float punkt = 4f * pixel;
					GUI.DrawTexture(new Rect(bm.X - punkt * 0.5f, bm.Y - punkt * 0.5f, punkt, punkt), _dot);
				}

				Rect platz = new Rect(bm.X - Mathf.Round(groesse.x * 0.5f),
									  bm.Y - groesse.y - 6f * pixel,
									  groesse.x, groesse.y);
				platz.x = Mathf.Clamp(platz.x, 0f, (float)Screen.width - groesse.x);

				// Frei nach oben suchen, sonst nach unten.
				if (!FreiMachen(ref platz, groesse.y))
				{
					continue;   // kein Platz - lieber weglassen als uebereinander
				}
				_belegt.Add(platz);

				// Fuehrungsstrich vom Punkt zur verschobenen Schrift, damit die
				// Zuordnung eindeutig bleibt.
				float mitte = platz.y + Mathf.Round(groesse.y * 0.5f);
				if (Mathf.Abs(mitte - bm.Y) > groesse.y)
				{
					float oben = Mathf.Min(mitte, bm.Y);
					float hoch = Mathf.Abs(mitte - bm.Y);
					GUI.DrawTexture(new Rect(bm.X, oben, pixel, hoch), _dot);
				}

				// Umriss: dieselbe Schrift viermal dunkel, um je einen Punkt
				// versetzt, darueber die farbige. Liest sich auf hellem Himmel
				// genauso wie auf dunklem Boden.
				_style.normal.textColor = new Color(schatten.r, schatten.g, schatten.b, schatten.a * farbe.a);
				GUI.Label(new Rect(platz.x - pixel, platz.y, platz.width, platz.height), text, _style);
				GUI.Label(new Rect(platz.x + pixel, platz.y, platz.width, platz.height), text, _style);
				GUI.Label(new Rect(platz.x, platz.y - pixel, platz.width, platz.height), text, _style);
				GUI.Label(new Rect(platz.x, platz.y + pixel, platz.width, platz.height), text, _style);
				_style.normal.textColor = farbe;
				GUI.Label(platz, text, _style);
				gezeichnet++;
			}
		}

		/// <summary>
		/// Schiebt die Beschriftung so weit nach oben (ersatzweise nach unten),
		/// bis sie keine bereits gesetzte mehr ueberlappt. Liefert false, wenn
		/// im vertretbaren Bereich kein Platz frei ist.
		/// </summary>
		private bool FreiMachen(ref Rect platz, float zeilenhoehe)
		{
			float schritt = Mathf.Round(zeilenhoehe + 2f);
			for (int richtung = 0; richtung < 2; richtung++)
			{
				Rect versuch = platz;
				for (int n = 0; n < 12; n++)
				{
					bool frei = true;
					for (int k = 0; k < _belegt.Count; k++)
					{
						if (versuch.Overlaps(_belegt[k]))
						{
							frei = false;
							break;
						}
					}
					if (frei && versuch.y >= 0f && versuch.yMax <= (float)Screen.height)
					{
						platz = versuch;
						return true;
					}
					versuch.y += (richtung == 0) ? (0f - schritt) : schritt;
				}
			}
			return false;
		}

		private Color ColorFor(MarkerKind kind)
		{
			return kind switch
			{
				MarkerKind.QuestRequired => _colorRequired.Value,
				MarkerKind.SpaeterNoetig => _colorSpaeter.Value,
				MarkerKind.Zone => _colorZone.Value,
				MarkerKind.SchluesselFehlt => _colorSchluesselFehlt.Value,
				MarkerKind.SchluesselDa => _colorSchluesselDa.Value,
				MarkerKind.Ablegen => ZonenFarbe(_colorAblegen),
				MarkerKind.Peilsender => ZonenFarbe(_colorPeilsender),
				MarkerKind.Leuchtkugel => ZonenFarbe(_colorLeuchtkugel),
				MarkerKind.Mitbringen => _colorMitbringen.Value,
				_ => _colorFlagged.Value,
			};
		}

		/// <summary>Ortsziele sind die Wegweiser: Orte, zu denen eine Aufgabe
		/// schickt. Sie bekommen beim Beschriften Vorrang und duerfen die
		/// Entfernungsgrenze ueberspringen.</summary>
		private static bool IstOrtsziel(MarkerKind art)
		{
			switch (art)
			{
				case MarkerKind.Zone:
				case MarkerKind.Ablegen:
				case MarkerKind.Peilsender:
				case MarkerKind.Leuchtkugel:
				case MarkerKind.Ausgang:
					return true;
				default:
					return false;
			}
		}

		/// <summary>Die Farbe eines Ortsziels: entweder die gemeinsame
		/// Zonenfarbe oder die eigene der Art. Die Arten unterscheiden sich
		/// ohnehin schon durch ihr Zeichen - die Farbe darf also
		/// zusammenfassen, ohne dass etwas verwechselbar wird.</summary>
		private Color ZonenFarbe(ConfigEntry<Color> eigene)
		{
			if (_zonenEinfarbig != null && _zonenEinfarbig.Value)
			{
				return _colorZone.Value;
			}
			return eigene.Value;
		}

		/// <summary>Dieselbe Frage, aber mit Blick auf den Zustand: Ausgaenge
		/// faerben sich danach, ob sie offen sind, gerade herunterzaehlen
		/// oder noch nicht freigeschaltet wurden.</summary>
		private Color ColorFor(Marker marke)
		{
			if (marke.Kind != MarkerKind.Fluchtpunkt && marke.Kind != MarkerKind.Ausgang)
			{
				return ColorFor(marke.Kind);
			}
			switch (marke.Zustand)
			{
				case Ausgangszustand.Offen:
					return _colorAusgangOffen.Value;
				case Ausgangszustand.Countdown:
					return _colorAusgangCountdown.Value;
				case Ausgangszustand.Gesperrt:
					return _colorAusgangGesperrt.Value;
				default:
					// Ein aufgabenbezogener Ausgang ohne Zustand bleibt in der
					// Farbe der Aufgabe.
					return marke.Kind == MarkerKind.Ausgang
						? _colorZone.Value
						: _colorAusgangGesperrt.Value;
			}
		}

		private void EnsureStyle()
		{
			if (_style == null)
			{
				_style = new GUIStyle(GUI.skin.label)
				{
					fontSize = _fontSize.Value,
					fontStyle = FontStyle.Bold,
					alignment = TextAnchor.MiddleCenter,
					richText = false
				};
				if (_dot == null)
				{
					_dot = new Texture2D(1, 1);
					_dot.SetPixel(0, 0, Color.white);
					_dot.Apply();
				}
			}
		}

		private void OnDestroy()
		{
			WeltmarkenAufraeumen();
			if (_dot != null)
			{
				UnityEngine.Object.Destroy(_dot);
			}
		}
	}
}
