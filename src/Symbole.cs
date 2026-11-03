// ---------------------------------------------------------------
//  Symbole - ein eigenes Bild je Zielart
//
//  HERKUNFT: Die Strichzeichnungen stammen aus dem Mod QuestMarkers
//  von maschine (MIT-Lizenz), dort in web/markers.html als SVG. Sie
//  liegen hier als PNG vor (symbole/png, erzeugt von
//  werkzeuge/symbole-bauen.sh) und sind in die DLL eingebettet.
//  Nachgezeichnet im selben Stil, weil es dort kein Gegenstueck gab:
//  der Schluessel.
//
//  Die Bilder sind WEISS auf durchsichtig. Eingefaerbt wird zur
//  Laufzeit ueber die Vertexfarbe (sr.color) - so teilen sich alle
//  Marken ein einziges Material, egal welche Farbe sie haben.
//
//  OB SYMBOLE UEBERHAUPT GEHEN, haengt am Shader:
//  Hidden/Internal-Colored - bisher unser Rueckfall in Tarkov - kann
//  gar keine Textur. Dort bleibt es beim einfarbigen Punkt von
//  frueher. Welcher Shader es geworden ist, steht bei eingeschaltetem
//  Log im BepInEx-Fenster; siehe PunktStoffBauen in Weltmarken.cs.
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace QuestItemESP
{
	public partial class QuestItemEspPlugin
	{
		// Der alte Punkt ist ein 1x1-Bild; bei den Unity-Standardwerten
		// (100 Bildpunkte je Einheit) ist er 0,01 Einheiten gross. Die
		// Symbole muessen GENAU SO gross ausfallen, sonst stimmen
		// Strichlaenge und Schrifthoehe nicht mehr, die anderswo aus
		// dieser Groesse errechnet werden. 128 Bildpunkte auf 0,01
		// Einheiten macht 12800 Bildpunkte je Einheit.
		private const float SymbolPunkteJeEinheit = 12800f;

		private readonly Dictionary<MarkerKind, Sprite> _symbolBilder =
			new Dictionary<MarkerKind, Sprite>();

		private readonly Dictionary<MarkerKind, Texture2D> _symbolTexturen =
			new Dictionary<MarkerKind, Texture2D>();

		private bool _symboleGeladen;

		/// <summary>Ordnet jeder Zielart ihre Bilddatei zu. Der Name ist
		/// der Dateiname ohne Endung in symbole/png.</summary>
		private static string SymbolName(MarkerKind art)
		{
			switch (art)
			{
				case MarkerKind.Zone:
					return "zone";
				case MarkerKind.Ablegen:
					return "ablegen";
				case MarkerKind.Peilsender:
					return "peilsender";
				case MarkerKind.Leuchtkugel:
					return "leuchtkugel";
				case MarkerKind.Ausgang:
					return "ausgang";
				case MarkerKind.Fluchtpunkt:
					return "fluchtpunkt";
				case MarkerKind.SchluesselFehlt:
				case MarkerKind.SchluesselDa:
					return "schluessel";
				default:
					// QuestFlagged, QuestRequired, SpaeterNoetig - alles
					// Gegenstaende, die herumliegen. Unterschieden werden
					// sie ueber die Farbe.
					return "gegenstand";
			}
		}

		/// <summary>Laedt die eingebetteten Bilder. Laeuft einmal; scheitert
		/// sie, bleibt es beim einfarbigen Punkt.</summary>
		private void SymboleLaden()
		{
			if (_symboleGeladen)
			{
				return;
			}
			_symboleGeladen = true;

			Assembly mich = Assembly.GetExecutingAssembly();
			var schonGeladen = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
			int gut = 0;

			foreach (MarkerKind art in Enum.GetValues(typeof(MarkerKind)))
			{
				string name = SymbolName(art);
				Texture2D bild;
				if (!schonGeladen.TryGetValue(name, out bild))
				{
					bild = BildLaden(mich, name);
					schonGeladen[name] = bild;
				}
				if (bild == null)
				{
					continue;
				}
				_symbolTexturen[art] = bild;
				_symbolBilder[art] = Sprite.Create(bild,
					new Rect(0f, 0f, bild.width, bild.height),
					new Vector2(0.5f, 0.5f),
					SymbolPunkteJeEinheit);
				gut++;
			}

			if (gut == 0)
			{
				Log.LogWarning("Symbole: keine Bilder gefunden - Marken bleiben einfarbige Punkte.");
			}
			else if (_debugLog != null && _debugLog.Value)
			{
				Log.LogInfo("Symbole: " + schonGeladen.Count + " Bilder geladen, "
					+ gut + " Zielarten zugeordnet.");
			}
		}

		private static Texture2D BildLaden(Assembly mich, string name)
		{
			string pfad = "QuestItemESP.symbole." + name + ".png";
			try
			{
				using (Stream strom = mich.GetManifestResourceStream(pfad))
				{
					if (strom == null)
					{
						Log.LogWarning("Symbole: " + pfad + " steckt nicht in der DLL.");
						return null;
					}
					byte[] roh = new byte[strom.Length];
					int gelesen = 0;
					while (gelesen < roh.Length)
					{
						int n = strom.Read(roh, gelesen, roh.Length - gelesen);
						if (n <= 0)
						{
							break;
						}
						gelesen += n;
					}
					// mipChain aus: die Marke wird ohnehin frei skaliert, und
					// Stufen wuerden die duennen Striche verwaschen.
					Texture2D bild = new Texture2D(2, 2, TextureFormat.ARGB32, mipChain: false);
					if (!bild.LoadImage(roh))
					{
						UnityEngine.Object.Destroy(bild);
						Log.LogWarning("Symbole: " + name + ".png liess sich nicht lesen.");
						return null;
					}
					bild.wrapMode = TextureWrapMode.Clamp;
					bild.filterMode = FilterMode.Bilinear;
					bild.Apply(updateMipmaps: false, makeNoLongerReadable: false);
					return bild;
				}
			}
			catch (Exception ex)
			{
				Log.LogWarning("Symbole: " + name + " - " + ex.Message);
				return null;
			}
		}

		/// <summary>Das Sprite fuer die Weltmarken. null heisst: kein Bild
		/// vorhanden, der Aufrufer nimmt den einfarbigen Punkt.</summary>
		private Sprite SymbolBild(MarkerKind art)
		{
			SymboleLaden();
			Sprite s;
			return _symbolBilder.TryGetValue(art, out s) ? s : null;
		}

		/// <summary>Dieselbe Zeichnung als Textur - die braucht die alte
		/// Darstellung ueber OnGUI.</summary>
		private Texture2D SymbolTextur(MarkerKind art)
		{
			SymboleLaden();
			Texture2D t;
			return _symbolTexturen.TryGetValue(art, out t) ? t : null;
		}
	}
}
