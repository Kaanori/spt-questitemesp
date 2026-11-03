// ---------------------------------------------------------------
//  Weltmarken - Marken als echte Objekte in der Spielwelt
//
//  Bis 20.09.2026 hat die ESP die Weltposition jedes Fundes in
//  Bildschirmkoordinaten umgerechnet und die Marke ueber OnGUI
//  gezeichnet. Das ist fehleranfaellig: man muss genau die Kamera
//  erwischen, die das Bild zeichnet, und die Aufloesung der Kamera
//  kann von der Fensteraufloesung abweichen. Beides hat die Marken
//  vom Gegenstand weggeschoben, und zwar umso mehr, je weiter sie
//  vom Bildmittelpunkt entfernt waren.
//
//  AmandsSense macht es anders und deshalb richtig: Es setzt ein
//  echtes Objekt an den Fundort, dreht es zur Kamera und skaliert
//  es nach Entfernung. Die Spiel-Engine zeichnet es selbst - da
//  kann nichts verrutschen. Genau das wird hier nachgebaut.
//
//  EIN UNTERSCHIED ZU SENSE: Sense-Marken werden von Waenden
//  verdeckt (in der ganzen Sense-DLL steht kein einziger Eingriff
//  in den Tiefentest). Wir wollen sie durch Waende sehen.
//
//  DAFUER REICHT EIN TIEFENTEST NICHT - es sind DREI Huerden, und
//  jede einzelne laesst die Marke hinter der Wand verschwinden:
//
//   1. Sprites/Default kann es gar nicht. In diesem Shader steht
//      ZTest fest auf LEqual, und es gibt keine Eigenschaft, um ihn
//      von aussen zu aendern. Ein SetFloat laeuft ins Leere. Darum
//      bekommt der Punkt jetzt ein Material aus einem Shader, der
//      einen steuerbaren Tiefentest mitbringt - UI/Default nennt
//      ihn unity_GUIZTestMode.
//
//   2. TextMeshPro setzt den Tiefentest selbst wieder zurueck. In
//      SetShaderDepth() schreibt es "unity_GUIZTestMode" hart auf 4
//      (= nur zeichnen, wenn nichts davor liegt) - und ruft das bei
//      jedem Material- oder Schriftwechsel auf. Von aussen dagegen
//      anzuschreiben ist aussichtslos. TMP hat aber einen Schalter
//      dafuer: isOverlay. Steht der auf true, schreibt dieselbe
//      Methode eine 0 (= gar nicht pruefen). Den benutzen wir, statt
//      am Material zu drehen.
//
//      WICHTIG: erst tm.fontMaterial anfassen, dann isOverlay setzen.
//      fontMaterial legt eine eigene Kopie an; andernfalls wuerde der
//      Schalter das GETEILTE Material treffen und jede Schrift im
//      Spiel durch Waende scheinen lassen.
//
//   3. Occlusion Culling wirft die Marke ganz raus. Tarkov nutzt
//      Umbra: was hinter Geometrie liegt, wird erst gar nicht
//      gezeichnet - dann nuetzt der schoenste Tiefentest nichts.
//      Gegenmittel ist Renderer.allowOcclusionWhenDynamic = false.
// ---------------------------------------------------------------
using System.Collections.Generic;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;

namespace QuestItemESP
{
	public partial class QuestItemEspPlugin
	{
		// "Always" aus UnityEngine.Rendering.CompareFunction - der Wert, der
		// dem Tiefentest sagt: zeichne, egal was davor liegt.
		private const int TiefentestImmer = 8;

		private ConfigEntry<bool> _weltmarken;
		private ConfigEntry<float> _symbolGroesse;
		private ConfigEntry<float> _symbolFaktor;
		private ConfigEntry<float> _groessenGrenze;
		private ConfigEntry<float> _zonenGroessenGrenze;
		private ConfigEntry<float> _textGroesse;
		private ConfigEntry<bool> _verbindungslinie;
		private ConfigEntry<bool> _buendeln;
		private ConfigEntry<float> _buendelRadius;

		private GameObject _weltWurzel;
		private Sprite _punktBild;
		private Material _punktStoff;

		// Kann das gewaehlte Material ueberhaupt eine Textur zeigen? Nur
		// dann gibt es Symbole statt einfarbiger Punkte.
		private bool _stoffKannTextur;

		private bool _shaderGemeldet;
		private bool _sichtweiteGemeldet;
		private bool _schriftGemeldet;

		// Grundhoehe der Schrift ueber dem Punkt (lokale Einheiten). Von hier
		// aus weicht sie nach oben aus, wenn der Platz schon belegt ist.
		private const float TextGrundhoehe = 0.09f;

		/// <summary>Grundhoehe der Schrift, wie sie JETZT gilt. Ein Zeichen
		/// ist um ein Vielfaches groesser als der alte Punkt - bliebe die
		/// Schrift auf ihrer alten Hoehe, laege sie mitten im Zeichen.</summary>
		private float TextGrundhoeheJetzt()
		{
			if (!SymboleAktiv())
			{
				return TextGrundhoehe;
			}
			float punkthoehe = ((_punktBild != null) ? _punktBild.bounds.size.y : 0.01f);
			return TextGrundhoehe + punkthoehe * SymbolFaktor() * 0.5f;
		}

		private bool SymboleAktiv()
		{
			return _symboleZeigen != null && _symboleZeigen.Value && _stoffKannTextur;
		}

		private float SymbolFaktor()
		{
			return (_symbolFaktor != null) ? Mathf.Max(1f, _symbolFaktor.Value) : 1f;
		}

		// Staerke des Verbindungsstrichs, ebenfalls in lokalen Einheiten.
		// Der Punkt selbst ist eine Einheit breit - der Strich also gut ein
		// Drittel davon und damit deutlich duenner.
		private const float StrichBreite = 0.0035f;

		// Kuerzere Striche als diesen Wert bleiben aus: bei einer Schrift,
		// die gar nicht ausweichen musste, waere der Strich nur ein Klecks
		// direkt ueber dem Punkt.
		private const float StrichMindest = 0.03f;

		private readonly List<Entzerrung> _entzerrung = new List<Entzerrung>(64);
		private readonly List<Rect> _weltBelegt = new List<Rect>(64);

		private struct Entzerrung
		{
			public WeltMarke Marke;

			public float Entfernung;

			// Ortsziele bekommen ihren Namen zuerst - siehe NamenEntzerren.
			public bool Vorrang;
		}
		private readonly List<WeltMarke> _weltVorrat = new List<WeltMarke>();

		private sealed class WeltMarke
		{
			public GameObject Objekt;

			public SpriteRenderer Symbol;

			public SpriteRenderer Strich;

			public TextMeshPro Text;

			// Der Renderer der Schrift, einmal gemerkt. Das Nachzeichnen
			// braucht ihn in jedem Bild - GetComponent waere dort zu teuer.
			public Renderer TextZeichner;

			// Wofuer diese Marke steht. Das Entzerren braucht es, um
			// Ortszielen ihren Namen zu lassen.
			public MarkerKind Art;

			// Wo das Ziel WIRKLICH liegt. Das Objekt selbst kann naeher an
			// die Kamera gerueckt stehen - siehe SICHTWEITE in LateUpdate.
			public Vector3 EchterOrt;
		}

		private void WeltmarkenEinstellungenAnlegen()
		{
			KameraEinstellungenAnlegen();

			// Gehoert zur Darstellung und wirkt in BEIDEN Betriebsarten -
			// Weltmarken wie Oberflaechen-Anzeige.
			_buendeln = base.Config.Bind("3. Darstellung", "Gleiche zusammenfassen", defaultValue: true,
				"Fasst mehrere gleiche Funde am selben Ort zu einer Zeile zusammen\n"
				+ "und haengt die Stueckzahl an (z.B. \"M855 - noch 257  7x\").\n"
				+ "Auf einer Leiche liegen Patronen in mehreren Stapeln - ohne das\n"
				+ "steht dieselbe Zeile siebenmal untereinander.");
			_buendelRadius = base.Config.Bind("3. Darstellung", "Zusammenfassen bis", 2f,
				new ConfigDescription("Bis zu welchem Abstand (Meter) zwei gleiche Funde als\n"
					+ "derselbe Ort gelten. Groesser fasst mehr zusammen, kann aber\n"
					+ "zwei getrennte Verstecke zu einem verschmelzen.",
					new AcceptableValueRange<float>(0.5f, 20f)));
			// NEUER SCHLUESSEL (02.11.2026): Standard ist jetzt die Bildschirm-
			// Anzeige. BepInEx liest fuer einen bekannten Schluessel immer den
			// gespeicherten Wert - ein geaenderter Standard allein haette bei
			// jedem, der den alten Schalter schon in der cfg stehen hat, nichts
			// bewirkt. Der alte Eintrag "Marken in der Welt zeichnen" bleibt
			// verwaist in der Datei stehen und wird nicht mehr gelesen.
			_weltmarken = base.Config.Bind("4. Weltmarken", "Marken als Objekte in der Welt zeichnen", defaultValue: false,
				"Aus (Standard): Punkt und Schrift werden ueber die Oberflaeche in voller\n"
				+ "Bildschirmaufloesung gezeichnet - scharf, auch mit FSR/DLSS.\n"
				+ "An: die Marke ist ein echtes Objekt am Fundort, wie bei Sense. Sie entsteht\n"
				+ "dann mit der Szene in der Render-Aufloesung und wird mit ihr hochskaliert\n"
				+ "und geglaettet (FSR/DLSS, TAA) - deshalb wirkt sie weicher.");
			_symbolGroesse = base.Config.Bind("4. Weltmarken", "Symbolgroesse", 0.035f,
				new ConfigDescription("Groesse des Punktes. Wirkt gleichmaessig auf jede Entfernung.",
					new AcceptableValueRange<float>(0.005f, 0.2f)));
			_symbolFaktor = base.Config.Bind("4. Weltmarken", "Zeichengroesse", 30f,
				new ConfigDescription("Wie viel groesser das Zeichen ist als der alte Punkt.\n"
					+ "Der Punkt war ein voller Farbfleck und noch bei einem halben Bildpunkt\n"
					+ "zu erahnen; ein Zeichen besteht aus duennen Strichen und braucht\n"
					+ "ein Vielfaches an Flaeche, um lesbar zu sein. 30 ergibt etwa 18\n"
					+ "Bildpunkte Hoehe auf 30 Meter. Wirkt nur, wenn Zeichen angezeigt werden.",
					new AcceptableValueRange<float>(1f, 120f)));
			_groessenGrenze = base.Config.Bind("4. Weltmarken", "Groessenbegrenzung", 40f,
				new ConfigDescription("Ab dieser Entfernung (Meter) waechst die Marke nicht weiter mit.\n"
					+ "Ohne Begrenzung wuerden weit entfernte Marken riesig.",
					new AcceptableValueRange<float>(5f, 200f)));
			_zonenGroessenGrenze = base.Config.Bind("4. Weltmarken", "Groessenbegrenzung fuer Zonen", 1000f,
				new ConfigDescription("Dasselbe fuer Ortsziele - hinlaufen, ablegen, Peilsender,\n"
					+ "Leuchtkugel, Ausgang. Der Wert ist bewusst hoch: Eine Zone soll\n"
					+ "auch auf 300 Meter noch gleich gross im Bild stehen, denn genau\n"
					+ "dann sucht man sie. Bei 40 (wie oben) waere sie dort nur noch\n"
					+ "gut zwei Bildpunkte gross und praktisch unsichtbar.",
					new AcceptableValueRange<float>(5f, 2000f)));
			_verbindungslinie = base.Config.Bind("4. Weltmarken", "Verbindungslinie", defaultValue: true,
				"Zieht einen duennen Strich vom Namen hinunter zum Punkt.\n"
				+ "Hilfreich, sobald Namen weit ausweichen muessen, um sich nicht\n"
				+ "gegenseitig zu verdecken - sonst ist unklar, welcher Name zu\n"
				+ "welchem Fund gehoert.");
			_textGroesse = base.Config.Bind("4. Weltmarken", "Schriftgroesse", 2.2f,
				new ConfigDescription("Schriftgroesse der Beschriftung an der Marke.",
					new AcceptableValueRange<float>(0.5f, 8f)));
		}

		// Wie die Tiefentest-Eigenschaft heisst, haengt am Shader:
		// UI/Default nennt sie unity_GUIZTestMode, die TextMeshPro-Shader
		// _ZTestMode, Hidden/Internal-Colored schlicht _ZTest. Wir
		// probieren alle drei durch.
		private static readonly string[] TiefentestNamen =
		{
			"unity_GUIZTestMode",
			"_ZTestMode",
			"_ZTest"
		};

		/// <summary>Setzt den Tiefentest eines Materials auf "immer zeichnen".
		/// Liefert zurueck, ob der Shader das ueberhaupt zulaesst - genau
		/// daran ist es vorher gescheitert, ohne dass es aufgefallen waere.</summary>
		private static bool DurchWaendeZeichnen(Material stoff)
		{
			if (stoff == null)
			{
				return false;
			}
			bool gesetzt = false;
			for (int i = 0; i < TiefentestNamen.Length; i++)
			{
				if (stoff.HasProperty(TiefentestNamen[i]))
				{
					stoff.SetFloat(TiefentestNamen[i], TiefentestImmer);
					gesetzt = true;
				}
			}
			// Nach allem anderen zeichnen, damit die Marke nicht von
			// durchsichtigen Flaechen (Glas, Rauch) uebermalt wird.
			stoff.renderQueue = 4000;
			return gesetzt;
		}

		/// <summary>Nimmt einen Renderer aus dem Occlusion Culling heraus.
		/// Ohne das wird die Marke hinter Geometrie gar nicht erst
		/// gezeichnet - der Tiefentest kommt dann nie zum Zug.</summary>
		private static void NichtWegkuerzen(Renderer zeichner)
		{
			if (zeichner == null)
			{
				return;
			}
			zeichner.allowOcclusionWhenDynamic = false;
			zeichner.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
			zeichner.receiveShadows = false;
			// Ganz nach vorn innerhalb der Zeichenreihenfolge.
			zeichner.sortingOrder = 32767;
		}

		/// <summary>Baut das gemeinsame Material fuer den Punkt.
		/// Sprites/Default scheidet aus - siehe Huerde 1 im Kopf. Genommen
		/// wird der erste Shader, der einen steuerbaren Tiefentest hat;
		/// welcher es geworden ist, steht bei eingeschaltetem Log im
		/// BepInEx-Fenster.</summary>
		private Material PunktStoffBauen()
		{
			// Reihenfolge nach Faehigkeit, nicht nach Vorliebe: Ein Shader
			// taugt hier nur, wenn er DURCH WAENDE zeichnen kann. Kann er
			// zusaetzlich eine Textur, gibt es Symbole statt Punkte - genau
			// daran scheiterte es bisher, denn Hidden/Internal-Colored, unser
			// Rueckfall in Tarkov, kennt nur die Vertexfarbe.
			//
			// "GUI/Text Shader" ist der Sonderfall, der beides mitbringt: Er
			// hat ZTest Always FEST eingebaut (also keine Eigenschaft, die
			// man setzen koennte - HasProperty meldet brav "nein") und nimmt
			// die Textur als Alphamaske, waehrend die Farbe aus der
			// Vertexfarbe kommt. Genau die Kombination, die ein einfarbiges
			// Symbol braucht.
			string[] kandidaten = { "GUI/Text Shader", "UI/Default", "Sprites/Default", "Hidden/Internal-Colored" };
			bool[] tiefentestFest = { true, false, false, false };
			for (int i = 0; i < kandidaten.Length; i++)
			{
				Shader shader = Shader.Find(kandidaten[i]);
				if (shader == null)
				{
					continue;
				}
				Material stoff = new Material(shader);
				if (!DurchWaendeZeichnen(stoff) && !tiefentestFest[i])
				{
					// Kann keinen Tiefentest - naechster Kandidat.
					Object.Destroy(stoff);
					continue;
				}
				_stoffKannTextur = stoff.HasProperty("_MainTex");
				// Hidden/Internal-Colored zeichnet ab Werk deckend und mit
				// Tiefenschreiben. Beides waere hier falsch.
				if (stoff.HasProperty("_SrcBlend"))
				{
					stoff.SetFloat("_SrcBlend", 5f); // SrcAlpha
				}
				if (stoff.HasProperty("_DstBlend"))
				{
					stoff.SetFloat("_DstBlend", 10f); // OneMinusSrcAlpha
				}
				if (stoff.HasProperty("_ZWrite"))
				{
					stoff.SetFloat("_ZWrite", 0f);
				}
				// Bewusst ohne Debug-Schalter: die Zeile laeuft nur ein
				// einziges Mal und beantwortet die Frage, warum Symbole
				// erscheinen oder eben nicht.
				if (!_shaderGemeldet)
				{
					_shaderGemeldet = true;
					float gesetzt = -1f;
					for (int k = 0; k < TiefentestNamen.Length; k++)
					{
						if (stoff.HasProperty(TiefentestNamen[k]))
						{
							gesetzt = stoff.GetFloat(TiefentestNamen[k]);
							break;
						}
					}
					Log.LogInfo("Weltmarken: Punkt-Shader \"" + kandidaten[i]
						+ "\", Tiefentest " + (tiefentestFest[i] ? "fest auf Always" : "steuerbar, Wert=" + gesetzt)
						+ " (8 = durch Waende), Textur "
						+ (_stoffKannTextur ? "moeglich - Symbole an" : "NICHT moeglich - nur Punkte")
						// GUI/Text Shader nimmt seine Farbe aus der Material-
						// Eigenschaft _Color statt aus der Vertexfarbe. Trifft
						// das zu, sind alle Zeichen gleich eingefaerbt - dann
						// braucht es einen MaterialPropertyBlock je Marke.
						+ ", _Color " + (stoff.HasProperty("_Color") ? "vorhanden" : "nicht vorhanden") + ".");
				}
				return stoff;
			}
			Log.LogWarning("Weltmarken: kein Shader mit steuerbarem Tiefentest gefunden - "
				+ "Punkte bleiben hinter Waenden verborgen.");
			return null;
		}

		private Shader _schriftUeberShader;

		private bool _schriftUeberGesucht;

		/// <summary>Sorgt dafuer, dass die SCHRIFT durch Waende geht.
		///
		/// WARUM NICHT WIE BISHER: isOverlay setzt unity_GUIZTestMode auf 0,
		/// und 0 soll "nicht pruefen" heissen. In Tarkov wirkt das aber wie
		/// ein normaler Tiefentest - am 23.09.2026 ging das Zeichen durch die
		/// Wand, der Text daneben wurde von Gitterstaeben zerschnitten.
		/// Solange das Nachzeichnen ganz am Bildende lief, fiel das nicht
		/// auf; seit es vor der Nachbearbeitung laeuft, zaehlt der
		/// Tiefenpuffer wieder.
		///
		/// Loesung wie beim Zeichen: ein Shader, der "immer zeichnen" FEST
		/// eingebaut hat. TextMesh Pro bringt ihn mit ("Distance Field
		/// Overlay"), und er liegt im Spiel. Rueckfall, falls er fehlt: den
		/// Wert nach isOverlay von Hand auf 8 (= Always) setzen.</summary>
		private void SchriftDurchWaende(Material stoff)
		{
			if (stoff == null)
			{
				return;
			}
			if (!_schriftUeberGesucht)
			{
				_schriftUeberGesucht = true;
				_schriftUeberShader = Shader.Find("TextMeshPro/Distance Field Overlay");
				if (_schriftUeberShader == null)
				{
					// TextMesh Pro legt ihn in seinen Resources-Ordner.
					_schriftUeberShader = Resources.Load<Shader>("Shaders/TMP_SDF Overlay");
				}
				Log.LogInfo("Weltmarken: Schrift-Overlay-Shader "
					+ ((_schriftUeberShader != null) ? "gefunden - Schrift geht durch Waende"
						: "NICHT gefunden - Rueckfall auf Tiefentest-Wert 8"));
			}
			if (_schriftUeberShader != null && stoff.shader != _schriftUeberShader)
			{
				stoff.shader = _schriftUeberShader;
			}
			// In jedem Fall auch die Werte setzen - schadet beim Overlay-
			// Shader nicht und ist der Rueckfall, falls er fehlt. WICHTIG:
			// NACH isOverlay, sonst schreibt TextMesh Pro die 0 drueber.
			DurchWaendeZeichnen(stoff);
		}

		private WeltMarke WeltmarkeBauen()
		{
			if (_weltWurzel == null)
			{
				_weltWurzel = new GameObject("QuestItemESP-Marken");
				Object.DontDestroyOnLoad(_weltWurzel);
			}
			if (_punktBild == null)
			{
				Texture2D bild = new Texture2D(1, 1);
				bild.SetPixel(0, 0, Color.white);
				bild.Apply();
				_punktBild = Sprite.Create(bild, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f));
			}

			GameObject wurzel = new GameObject("Marke");
			wurzel.transform.SetParent(_weltWurzel.transform, worldPositionStays: false);

			GameObject punkt = new GameObject("Punkt");
			punkt.transform.SetParent(wurzel.transform, worldPositionStays: false);
			SpriteRenderer sr = punkt.AddComponent<SpriteRenderer>();
			sr.sprite = _punktBild;
			if (_punktStoff == null)
			{
				_punktStoff = PunktStoffBauen();
			}
			if (_punktStoff != null)
			{
				// Bewusst sharedMaterial: alle Marken teilen sich das eine
				// Material. Die Farbe steckt in sr.color (Vertexfarbe), nicht
				// im Material - sonst braeuchte jede Marke eine eigene Kopie.
				sr.sharedMaterial = _punktStoff;
			}
			else
			{
				DurchWaendeZeichnen(sr.material);
			}
			NichtWegkuerzen(sr);

			// Der Strich ist derselbe 1x1-Punkt, nur schmal und lang gezogen.
			// Kein LineRenderer noetig: die Marke ist immer zur Bildebene
			// gedreht und die Schrift weicht nur senkrecht aus - der Strich
			// steht also stets genau aufrecht.
			GameObject strich = new GameObject("Strich");
			strich.transform.SetParent(wurzel.transform, worldPositionStays: false);
			SpriteRenderer str = strich.AddComponent<SpriteRenderer>();
			str.sprite = _punktBild;
			if (_punktStoff != null)
			{
				str.sharedMaterial = _punktStoff;
			}
			else
			{
				DurchWaendeZeichnen(str.material);
			}
			NichtWegkuerzen(str);
			str.enabled = false;

			GameObject schrift = new GameObject("Schrift");
			schrift.transform.SetParent(wurzel.transform, worldPositionStays: false);
			// Etwas ueber den Punkt, damit die Schrift den Gegenstand nicht verdeckt.
			schrift.transform.localPosition = new Vector3(0f, TextGrundhoeheJetzt(), 0f);
			TextMeshPro tm = schrift.AddComponent<TextMeshPro>();
			tm.alignment = TextAlignmentOptions.Center;
			tm.enableWordWrapping = false;
			tm.overflowMode = TextOverflowModes.Overflow;
			tm.fontSize = _textGroesse.Value;
			tm.rectTransform.sizeDelta = new Vector2(20f, 1.5f);
			// Reihenfolge ist entscheidend: fontMaterial ZUERST - das legt
			// die eigene Materialkopie an. Erst danach isOverlay, sonst
			// traefe der Schalter das geteilte Material des ganzen Spiels.
			Material schriftStoff = tm.fontMaterial;
			tm.isOverlay = true;
			SchriftDurchWaende(schriftStoff);
			Renderer schriftZeichner = tm.GetComponent<Renderer>();
			NichtWegkuerzen(schriftZeichner);

			if (_debugLog != null && _debugLog.Value && !_schriftGemeldet)
			{
				_schriftGemeldet = true;
				Shader sh = ((schriftStoff != null) ? schriftStoff.shader : null);
				float wert = ((schriftStoff != null && schriftStoff.HasProperty("unity_GUIZTestMode"))
					? schriftStoff.GetFloat("unity_GUIZTestMode")
					: -1f);
				Log.LogInfo("Weltmarken: Schrift-Shader \""
					+ ((sh != null) ? sh.name : "?")
					+ "\", isOverlay=" + tm.isOverlay
					+ ", unity_GUIZTestMode=" + wert
					+ " (0 oder 8 = durch Waende, 4 = verdeckt)");
			}

			WeltMarke m = new WeltMarke
			{
				Objekt = wurzel,
				Symbol = sr,
				Strich = str,
				Text = tm,
				TextZeichner = schriftZeichner
			};
			// In den eigenen Layer, damit die Marken-Kamera sie findet und
			// sonst niemand sie zeichnet.
			InMarkenLayer(wurzel);

			_weltVorrat.Add(m);
			return m;
		}

		/// <summary>Haengt der Marke das Zeichen ihrer Zielart an. Kann der
		/// Shader keine Textur, bleibt der einfarbige Punkt stehen - dann
		/// unterscheiden nur die Farben.</summary>
		private void SymbolSetzen(WeltMarke marke, MarkerKind art)
		{
			if (marke.Symbol == null)
			{
				return;
			}
			Sprite gewuenscht = _punktBild;
			bool mitZeichen = false;
			if (SymboleAktiv())
			{
				Sprite zeichen = SymbolBild(art);
				if (zeichen != null)
				{
					gewuenscht = zeichen;
					mitZeichen = true;
				}
			}
			// Nur bei Wechsel zuweisen: SpriteRenderer.sprite baut sonst in
			// jedem Durchlauf sein Netz neu auf.
			if (marke.Symbol.sprite != gewuenscht)
			{
				marke.Symbol.sprite = gewuenscht;
			}

			// Ein Zeichen muss VIEL groesser sein als der alte Punkt. Der war
			// ein voller Farbfleck und auf 30 Metern gerade einen halben
			// Bildpunkt hoch - als Fleck noch zu erahnen, als Strichzeichnung
			// unsichtbar. Der Faktor sitzt am Zeichen selbst, nicht an der
			// Wurzel: Strich und Schrift sollen ihre Groesse behalten.
			float soll = mitZeichen ? SymbolFaktor() : 1f;
			if (!Mathf.Approximately(marke.Symbol.transform.localScale.x, soll))
			{
				marke.Symbol.transform.localScale = Vector3.one * soll;
			}

			if (!_zeichenGemeldet && mitZeichen)
			{
				_zeichenGemeldet = true;
				Texture2D t = marke.Symbol.sprite.texture;
				Log.LogInfo("Weltmarken: erstes Zeichen gesetzt - \"" + marke.Symbol.sprite.name
					+ "\", Textur " + ((t != null) ? t.width + "x" + t.height : "FEHLT")
					+ ", Hoehe " + marke.Symbol.sprite.bounds.size.y.ToString("F4")
					+ " Einheiten, Faktor " + soll.ToString("F0") + ".");
			}
		}

		private bool _zeichenGemeldet;

		/// <summary>Bringt die Weltobjekte auf den Stand der Markerliste.
		/// Objekte werden wiederverwendet statt jedes Mal neu gebaut -
		/// staendiges Erzeugen und Wegwerfen wuerde ruckeln.</summary>
		private void WeltmarkenAbgleichen()
		{
			if (_weltmarken == null || !_weltmarken.Value)
			{
				WeltmarkenVerstecken();
				return;
			}

			int gebraucht = (_enabled.Value ? _markers.Count : 0);
			for (int i = 0; i < gebraucht; i++)
			{
				WeltMarke m = ((i < _weltVorrat.Count) ? _weltVorrat[i] : WeltmarkeBauen());
				Marker quelle = _markers[i];

				m.EchterOrt = quelle.Position;
				m.Objekt.transform.position = quelle.Position;
				if (!m.Objekt.activeSelf)
				{
					m.Objekt.SetActive(value: true);
				}

				Color farbe = ColorFor(quelle);
				m.Symbol.color = farbe;
				m.Art = quelle.Kind;
				SymbolSetzen(m, quelle.Kind);
				if (m.Strich != null)
				{
					// Etwas blasser als Punkt und Schrift - der Strich soll
					// fuehren, nicht ins Auge springen.
					Color blass = farbe;
					blass.a = farbe.a * 0.55f;
					m.Strich.color = blass;
				}

				string text = quelle.Label;
				if (_showDistance.Value)
				{
					text = text + "  " + Mathf.RoundToInt(quelle.Distance) + " m";
				}
				if (m.Text.text != text)
				{
					m.Text.text = text;
					// TextMeshPro baut sein Material beim Umbruch neu auf und
					// wirft die Einstellung dabei weg - also nachziehen.
					SchriftDurchWaende(m.Text.fontMaterial);
					// Sofort neu ausmessen: das Entzerren unten braucht die
					// echten Masse noch in diesem Bild, sonst rechnet es mit
					// denen des vorherigen Textes.
					m.Text.ForceMeshUpdate();
				}
				m.Text.color = farbe;
				if (!Mathf.Approximately(m.Text.fontSize, _textGroesse.Value))
				{
					m.Text.fontSize = _textGroesse.Value;
				}
			}

			for (int i = gebraucht; i < _weltVorrat.Count; i++)
			{
				if (_weltVorrat[i].Objekt.activeSelf)
				{
					_weltVorrat[i].Objekt.SetActive(value: false);
				}
			}
		}

		private void WeltmarkenVerstecken()
		{
			for (int i = 0; i < _weltVorrat.Count; i++)
			{
				if (_weltVorrat[i].Objekt != null && _weltVorrat[i].Objekt.activeSelf)
				{
					_weltVorrat[i].Objekt.SetActive(value: false);
				}
			}
		}

		/// <summary>Ausrichten und Groesse - in jedem Bild, nach der Kamera.
		/// LateUpdate, weil die Kamera in Update noch bewegt werden kann.</summary>
		private void LateUpdate()
		{
			if (_weltmarken == null || !_weltmarken.Value || _weltVorrat.Count == 0)
			{
				return;
			}
			Camera kamera = GetGameCamera();
			if (kamera == null)
			{
				return;
			}
			Transform blick = kamera.transform;
			Vector3 augen = blick.position;

			_entzerrung.Clear();
			for (int i = 0; i < _weltVorrat.Count; i++)
			{
				GameObject o = _weltVorrat[i].Objekt;
				if (o == null || !o.activeSelf)
				{
					continue;
				}
				// Genau parallel zur Bildebene - so macht es Sense auch.
				// LookAt waere leicht schief, weil es zur Kameraposition
				// statt zur Blickrichtung dreht.
				o.transform.rotation = blick.rotation;

				// SICHTWEITE: Die Kamera zeichnet nichts jenseits ihrer
				// hinteren Grenze (farClipPlane). Diese Grenze ist eine EBENE,
				// gemessen wird die Tiefe geradeaus, nicht der echte Abstand.
				// Eine Zone in 505 m war deshalb am Bildrand (Tiefe 374 m)
				// sichtbar und verschwand, sobald man hinschaute (Tiefe 505 m)
				// - gemeldet am 23.09.2026.
				// Gegenmittel: Liegt das Ziel zu weit, steht die Marke auf
				// DERSELBEN Sichtlinie naeher an der Kamera. Weil sie unten
				// mit der Entfernung mitskaliert wird, sieht sie im Bild genau
				// gleich aus; die Beschriftung nennt weiter die echte
				// Entfernung, die kommt aus der Markerliste.
				Vector3 ziel = _weltVorrat[i].EchterOrt;
				Vector3 weg = ziel - augen;
				float echteEntfernung = weg.magnitude;
				float reichweite = kamera.farClipPlane * 0.8f;
				if (!_sichtweiteGemeldet)
				{
					_sichtweiteGemeldet = true;
					Log.LogInfo("Weltmarken: Sichtweite der Kamera " + kamera.farClipPlane.ToString("F0")
						+ " m - Marken weiter weg werden auf " + reichweite.ToString("F0") + " m herangeholt.");
				}
				if (echteEntfernung > reichweite && echteEntfernung > 0.01f)
				{
					o.transform.position = augen + weg * (reichweite / echteEntfernung);
				}
				else if (o.transform.position != ziel)
				{
					o.transform.position = ziel;
				}

				// Mit der Entfernung mitwachsen, damit die Marke auf jede
				// Entfernung gleich gross wirkt - bis zur Begrenzung.
				float entfernung = Vector3.Distance(augen, o.transform.position);
				// Die Begrenzung haelt die Marke ab dieser Entfernung auf
				// gleicher Weltgroesse - im BILD schrumpft sie danach wieder,
				// und zwar schnell: auf 300 m bleiben von 18 Bildpunkten noch
				// 2,4 uebrig. Ein voller Punkt war so gerade noch zu erahnen,
				// eine Strichzeichnung nicht mehr.
				//
				// Fuer Ortsziele ist das falsch: Eine Quest-Zone steht fast
				// immer weit weg, und genau dann braucht man sie. Sie bekommt
				// deshalb ihre eigene, viel weitere Grenze - damit bleibt sie
				// im Bild gleich gross, egal wie weit.
				float grenze = IstOrtsziel(_weltVorrat[i].Art)
					? _zonenGroessenGrenze.Value
					: _groessenGrenze.Value;
				float faktor = Mathf.Min(grenze, entfernung) * _symbolGroesse.Value;
				o.transform.localScale = Vector3.one * Mathf.Max(faktor, 0.001f);

				_entzerrung.Add(new Entzerrung
				{
					Marke = _weltVorrat[i],
					Entfernung = echteEntfernung,
					Vorrang = IstOrtsziel(_weltVorrat[i].Art)
				});
			}

			NamenEntzerren(kamera, blick);
			MarkenKameraAbgleichen(kamera);
		}

		/// <summary>Sorgt dafuer, dass sich die Beschriftungen nicht
		/// gegenseitig ueberdecken.
		///
		/// WARUM NOETIG: Die Marken stehen an ihren Fundorten. Liegen
		/// mehrere Funde in derselben Blickrichtung - etwa eine Reihe
		/// Kisten oder ein Haus voller Gegenstaende - landen ihre Namen im
		/// Bild uebereinander auf einer Linie und keiner ist mehr lesbar.
		/// Die Punkte bleiben deshalb, wo sie sind; nur die Schrift weicht
		/// nach oben aus. Dieselbe Idee wie FreiMachen bei der alten
		/// Oberflaechen-Anzeige, aber im Sichtfeld-Raum (0 bis 1) statt in
		/// Pixeln - damit ist es von der Aufloesungsskalierung unabhaengig.
		///
		/// Naechste zuerst: der Fund direkt vor dir bekommt seinen Platz,
		/// weiter entfernte weichen aus.</summary>
		private void NamenEntzerren(Camera kamera, Transform blick)
		{
			// Ortsziele zuerst, darin die naechsten zuerst. GRUND: Ueber der
			// Grenze faellt nur die SCHRIFT weg, das Zeichen bleibt stehen.
			// Zonen liegen fast immer weiter weg als die Gegenstaende vor
			// der Nase - nach reiner Entfernung sortiert verloeren sie ihren
			// Namen als erste, und ein Wegweiser ohne Beschriftung nuetzt
			// nichts.
			_entzerrung.Sort(delegate (Entzerrung a, Entzerrung b)
			{
				if (a.Vorrang != b.Vorrang)
				{
					return a.Vorrang ? -1 : 1;
				}
				return a.Entfernung.CompareTo(b.Entfernung);
			});
			_weltBelegt.Clear();

			int erlaubt = Mathf.Max(0, _maxLabels.Value);
			// Gezaehlt wird, was WIRKLICH beschriftet im Bild steht - nicht der
			// Platz in der Liste. Bis 23.09.2026 zaehlte der Listenplatz: Seit
			// Zonen Vorrang haben und keine Entfernungsgrenze mehr, belegten
			// die Zonen der ganzen Karte - auch die hinter dir - alle Plaetze,
			// und die Gegenstaende direkt vor dir standen ohne Namen da.
			int vergeben = 0;

			for (int i = 0; i < _entzerrung.Count; i++)
			{
				WeltMarke marke = _entzerrung[i].Marke;
				TextMeshPro schrift = marke.Text;
				if (schrift == null)
				{
					continue;
				}
				if (string.IsNullOrEmpty(schrift.text))
				{
					// Ueber der Grenze nur noch den Punkt zeigen - lieber
					// zwoelf lesbare Namen als vierzig Striche.
					SchriftAus(marke);
					continue;
				}

				// Immer erst zurueck auf die Grundhoehe. Ohne das wuerde der
				// Versatz des letzten Bildes mitgeschleppt und die Schrift
				// wanderte Bild fuer Bild weiter nach oben.
				schrift.transform.localPosition = new Vector3(0f, TextGrundhoeheJetzt(), 0f);

				Rect platz;
				float lokalProSicht;
				if (!SchriftFlaeche(kamera, blick, schrift, out platz, out lokalProSicht))
				{
					// Nicht im Bild - kostet keinen Platz.
					SchriftAus(marke);
					continue;
				}
				if (vergeben >= erlaubt)
				{
					// Ueber der Grenze nur noch das Zeichen zeigen - lieber
					// zwoelf lesbare Namen als vierzig Striche.
					SchriftAus(marke);
					continue;
				}

				float grund = platz.y;
				float schritt = platz.height + 0.004f;
				bool frei = false;

				// Erst nach oben ausweichen, dann nach unten.
				for (int richtung = 0; richtung < 2 && !frei; richtung++)
				{
					Rect versuch = platz;
					for (int n = 0; n < 12; n++)
					{
						if (!Ueberlappt(versuch) && versuch.y >= 0f && versuch.yMax <= 1f)
						{
							platz = versuch;
							frei = true;
							break;
						}
						versuch.y += ((richtung == 0) ? schritt : (0f - schritt));
					}
				}

				if (!frei)
				{
					SchriftAus(marke);
					continue;
				}

				float versatz = (platz.y - grund) * lokalProSicht;
				schrift.transform.localPosition = new Vector3(0f, TextGrundhoeheJetzt() + versatz, 0f);
				schrift.enabled = true;
				_weltBelegt.Add(platz);
				vergeben++;

				// Der Strich endet an der Unterkante der Schrift, nicht in
				// ihrer Mitte - sonst liefe er in die Buchstaben hinein.
				// textBounds.min.y ist negativ, deshalb ein Plus.
				StrichSetzen(marke, TextGrundhoeheJetzt() + versatz + schrift.textBounds.min.y);
			}
		}

		/// <summary>Blendet Schrift und zugehoerigen Strich aus. Der Punkt
		/// bleibt stehen - man soll weiter sehen, wo der Fund liegt.</summary>
		private static void SchriftAus(WeltMarke marke)
		{
			if (marke.Text != null && marke.Text.enabled)
			{
				marke.Text.enabled = false;
			}
			if (marke.Strich != null && marke.Strich.enabled)
			{
				marke.Strich.enabled = false;
			}
		}

		/// <summary>Spannt den Verbindungsstrich vom Punkt (Hoehe 0) bis
		/// zur Unterkante der Schrift.</summary>
		private void StrichSetzen(WeltMarke marke, float bisHoehe)
		{
			if (marke.Strich == null)
			{
				return;
			}
			if (_verbindungslinie == null || !_verbindungslinie.Value || bisHoehe <= StrichMindest)
			{
				if (marke.Strich.enabled)
				{
					marke.Strich.enabled = false;
				}
				return;
			}

			// Der Punkt-Sprite ist nicht eine Einheit gross, sondern so
			// gross, wie Unity ihn aus Bildpunkten je Einheit errechnet.
			// Darum durch seine echte Groesse teilen, statt einen festen
			// Wert anzunehmen.
			float basis = ((_punktBild != null) ? _punktBild.bounds.size.y : 0.01f);
			if (basis <= 1E-06f)
			{
				basis = 0.01f;
			}

			marke.Strich.transform.localPosition = new Vector3(0f, bisHoehe * 0.5f, 0f);
			marke.Strich.transform.localScale = new Vector3(StrichBreite / basis, bisHoehe / basis, 1f);
			if (!marke.Strich.enabled)
			{
				marke.Strich.enabled = true;
			}
		}

		/// <summary>Misst, welche Flaeche die Schrift im Bild einnimmt
		/// (Sichtfeld-Koordinaten 0 bis 1), und liefert nebenbei den
		/// Umrechnungsfaktor von Sichtfeld-Hoehe in lokale Einheiten -
		/// den brauchen wir, um den gefundenen Platz wieder in eine
		/// Verschiebung des Objekts umzurechnen.</summary>
		private static bool SchriftFlaeche(Camera kamera, Transform blick, TextMeshPro schrift,
			out Rect flaeche, out float lokalProSicht)
		{
			flaeche = default(Rect);
			lokalProSicht = 0f;

			Bounds mass = schrift.textBounds;
			float skala = schrift.transform.lossyScale.y;
			if (mass.extents.y <= 0f || skala <= 0f)
			{
				return false;
			}

			Vector3 mitte = schrift.transform.TransformPoint(mass.center);
			Vector3 sichtMitte = kamera.WorldToViewportPoint(mitte);
			if (sichtMitte.z <= 0f)
			{
				return false;   // hinter der Kamera
			}
			// Seitlich oder oben/unten ausserhalb des Bildes: ebenfalls nicht
			// sichtbar. Ohne das hat eine Zone links neben dir einen der
			// Namensplaetze belegt, obwohl ihre Schrift nie zu sehen war.
			if (sichtMitte.x < -0.1f || sichtMitte.x > 1.1f || sichtMitte.y < -0.1f || sichtMitte.y > 1.1f)
			{
				return false;
			}

			// Halbe Hoehe und Breite entlang der Kameraachsen messen. Das
			// Markenobjekt ist zur Bildebene gedreht, darum stimmen seine
			// Achsen mit denen der Kamera ueberein.
			float halbHoheWelt = mass.extents.y * skala;
			float halbBreitWelt = mass.extents.x * skala;
			Vector3 sichtOben = kamera.WorldToViewportPoint(mitte + blick.up * halbHoheWelt);
			Vector3 sichtRechts = kamera.WorldToViewportPoint(mitte + blick.right * halbBreitWelt);

			float halbHoch = Mathf.Abs(sichtOben.y - sichtMitte.y);
			float halbBreit = Mathf.Abs(sichtRechts.x - sichtMitte.x);
			if (halbHoch < 1E-05f)
			{
				return false;
			}

			flaeche = new Rect(sichtMitte.x - halbBreit, sichtMitte.y - halbHoch,
				halbBreit * 2f, halbHoch * 2f);
			// Sichtfeld-Hoehe -> lokale Einheiten: die halbe Hoehe entspricht
			// im Bild halbHoch, im lokalen Raum mass.extents.y.
			lokalProSicht = mass.extents.y / halbHoch;
			return true;
		}

		private bool Ueberlappt(Rect flaeche)
		{
			for (int i = 0; i < _weltBelegt.Count; i++)
			{
				if (flaeche.Overlaps(_weltBelegt[i]))
				{
					return true;
				}
			}
			return false;
		}

		private void WeltmarkenAufraeumen()
		{
			for (int i = 0; i < _weltVorrat.Count; i++)
			{
				if (_weltVorrat[i].Objekt != null)
				{
					Object.Destroy(_weltVorrat[i].Objekt);
				}
			}
			_weltVorrat.Clear();
			if (_weltWurzel != null)
			{
				Object.Destroy(_weltWurzel);
				_weltWurzel = null;
			}
			if (_punktStoff != null)
			{
				Object.Destroy(_punktStoff);
				_punktStoff = null;
			}
			MarkenKameraAufraeumen();
		}
	}
}
