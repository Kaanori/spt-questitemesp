// ---------------------------------------------------------------
//  Bruecke zu AllQuestsCheckmarks
//
//  Der Mod rechnet im Inventar bereits aus, wie viele Stueck eines
//  Gegenstands eine Aufgabe verlangt und wie viele davon schon im
//  Lager liegen ("In Stash: 5 (5 FiR) / Total needed: 15 FiR").
//  Genau diese Rechnung uebernehmen wir, statt sie nachzubauen.
//
//  Zugriff ueber Reflexion: seine Klassen sind "internal", das ist
//  aber nur eine Regel des Uebersetzers - zur Laufzeit kommt man
//  ohne Weiteres heran. Ein Verweis auf seine DLL waere die
//  schlechtere Loesung: dann liefe unser Mod nicht mehr, wenn
//  AllQuestsCheckmarks fehlt.
// ---------------------------------------------------------------
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace QuestItemESP
{
	internal static class Checkmarks
	{
		/// <summary>Wie ein Gegenstand zur Aufgabenlage steht.</summary>
		public enum Lage
		{
			/// <summary>Keine Angabe - dann nicht ausblenden.</summary>
			Unbekannt,
			/// <summary>Wird noch gebraucht.</summary>
			NochNoetig,
			/// <summary>Bedarf ist gedeckt.</summary>
			Erledigt
		}

		private sealed class Bedarf
		{
			public int Fir;

			public int NonFir;
		}

		private static bool _gesucht;

		private static IDictionary _tabelle;

		private static FieldInfo _bedarfFir;

		private static FieldInfo _bedarfNonFir;

		// Pro Gegenstand fuehrt die Tabelle auch die Aufgaben, die ihn
		// verlangen - mit Namen. Genau damit laesst sich unterscheiden,
		// ob eine Marke zu etwas Laufendem gehoert oder nicht.
		private static FieldInfo _bedarfQuests;

		private static FieldInfo _questName;

		private static FieldInfo _questNameLokal;

		private static FieldInfo _questMenge;

		private static FieldInfo _mengeCount;

		private static FieldInfo _mengeFir;

		/// <summary>Was EINE Aufgabe von einem Gegenstand verlangt.</summary>
		public struct Aufgabenbedarf
		{
			public string Id;

			public string Name;

			public int Menge;

			/// <summary>Muss das Stueck im Einsatz gefunden sein?</summary>
			public bool Fir;
		}

		private static MethodInfo _bestandMethode;

		private static FieldInfo _bestandFir;

		private static FieldInfo _bestandNonFir;

		// Seine Einstellung "Include items in PMC inventory (in raid)": nur
		// wenn sie an ist, zaehlt GetItemsInStash im Raid auch mit, was man
		// am Mann traegt.
		private static FieldInfo _mitRaidSachen;

		/// <summary>Was EINE laufende Aufgabe laut Spiel von einer Vorlage
		/// noch braucht - aus dem Fortschritt der Bedingung, nicht aus der
		/// Gesamtmenge der Tabelle.</summary>
		public struct Restposten
		{
			/// <summary>Verlangt minus Fortschritt (value - CurrentValue).</summary>
			public double Rest;

			/// <summary>Abgabe-Bedingung (HandoverItem). Falsch = nur FindItem.</summary>
			public bool Abgabe;

			/// <summary>Muss das Stueck im Einsatz gefunden sein?</summary>
			public bool Fir;

			/// <summary>Falsch, wenn das Spiel fuer die Bedingung keinen
			/// Fortschritt liefert.</summary>
			public bool Bekannt;
		}

		public static string Zustand = "noch nicht gesucht";

		public static bool Vorhanden => _tabelle != null;

		// Der Bedarf aendert sich nur, wenn eine Aufgabe angenommen oder
		// abgegeben wird - alle 10 Sekunden neu einlesen reicht voellig.
		private static readonly Dictionary<string, Bedarf> Schnappschuss =
			new Dictionary<string, Bedarf>();

		private static float _schnappschussZeit = -999f;

		// Der Lagerbestand wird pro Vorlage gemerkt: die Abfrage laeuft
		// sonst zehnmal je Sekunde durch das ganze Inventar.
		private static readonly Dictionary<string, int[]> BestandsCache =
			new Dictionary<string, int[]>();

		private static float _bestandsZeit = -999f;

		private static float _suchZeit = -999f;

		private static void Suchen()
		{
			if (_gesucht)
			{
				return;
			}
			// Nicht dauerhaft aufgeben: laedt AllQuestsCheckmarks spaeter als
			// wir, waere der Zugriff sonst fuer immer verloren. Alle zehn
			// Sekunden noch einmal nachsehen kostet nichts.
			if (Time.time - _suchZeit < 10f)
			{
				return;
			}
			_suchZeit = Time.time;
			try
			{
				Assembly treffer = null;
				Assembly[] alle = AppDomain.CurrentDomain.GetAssemblies();
				for (int i = 0; i < alle.Length; i++)
				{
					string n = alle[i].GetName().Name;
					if (n != null && n.StartsWith("AllQuestsCheckmarks"))
					{
						// Es gibt zwei DLLs (Client und Fika). Die Daten
						// liegen in der, die QuestsData mitbringt.
						if (alle[i].GetType("AllQuestsCheckmarks.Helpers.QuestsData") != null)
						{
							treffer = alle[i];
							break;
						}
					}
				}
				if (treffer == null)
				{
					Zustand = "AllQuestsCheckmarks nicht geladen";
					return;
				}
				Type daten = treffer.GetType("AllQuestsCheckmarks.Helpers.QuestsData");
				Type lager = treffer.GetType("AllQuestsCheckmarks.Helpers.StashHelper");
				Type posten = treffer.GetType("AllQuestsCheckmarks.Helpers.QuestsData+ItemData");
				Type zaehler = treffer.GetType("AllQuestsCheckmarks.Helpers.StashHelper+ItemsCount");
				FieldInfo f = daten?.GetField("QuestItemsByItemId",
					BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				_tabelle = f?.GetValue(null) as IDictionary;
				_bedarfFir = posten?.GetField("Fir");
				_bedarfNonFir = posten?.GetField("NonFir");
				_bedarfQuests = posten?.GetField("Quests");
				Type questWerte = treffer.GetType("AllQuestsCheckmarks.Helpers.QuestsData+QuestValues");
				_questName = questWerte?.GetField("Name");
				_questNameLokal = questWerte?.GetField("LocalizedName");
				_questMenge = questWerte?.GetField("Count");
				Type mengeTyp = treffer.GetType("AllQuestsCheckmarks.Helpers.QuestsData+QuestItems");
				_mengeCount = mengeTyp?.GetField("Count");
				_mengeFir = mengeTyp?.GetField("Fir");
				_bestandMethode = lager?.GetMethod("GetItemsInStash",
					BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				_bestandFir = zaehler?.GetField("Fir");
				_bestandNonFir = zaehler?.GetField("NonFir");
				_mitRaidSachen = treffer.GetType("AllQuestsCheckmarks.Helpers.Settings")?.GetField("IncludeRaidItems",
					BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				if (_tabelle == null || _bedarfFir == null || _bedarfNonFir == null)
				{
					_tabelle = null;
					Zustand = "Tabelle QuestItemsByItemId nicht lesbar";
					return;
				}
				_gesucht = true;
				Zustand = "verbunden, " + _tabelle.Count + " Eintraege"
					+ ((_bestandMethode != null && _bestandFir != null)
						? ", Lagerbestand verfuegbar"
						: ", OHNE Lagerbestand")
					+ ((_bedarfQuests != null && _questNameLokal != null && _mengeCount != null)
						? ", Aufgabenzuordnung verfuegbar"
						: ", OHNE Aufgabenzuordnung"
							+ " (Quests=" + (_bedarfQuests != null)
							+ ", Name=" + (_questNameLokal != null)
							+ ", Menge=" + (_mengeCount != null) + ")");
			}
			catch (Exception ex)
			{
				_tabelle = null;
				Zustand = "Fehler: " + ex.Message;
			}
		}

		private static void SchnappschussPflegen()
		{
			if (_tabelle == null || Time.time - _schnappschussZeit < 10f)
			{
				return;
			}
			_schnappschussZeit = Time.time;
			Schnappschuss.Clear();
			try
			{
				IDictionaryEnumerator e = _tabelle.GetEnumerator();
				while (e.MoveNext())
				{
					if (e.Key == null || e.Value == null)
					{
						continue;
					}
					string id = ((MongoID)e.Key).ToString();
					if (string.IsNullOrEmpty(id))
					{
						continue;
					}
					Schnappschuss[id] = new Bedarf
					{
						Fir = (int)_bedarfFir.GetValue(e.Value),
						NonFir = (int)_bedarfNonFir.GetValue(e.Value)
					};
				}
			}
			catch (Exception ex)
			{
				QuestItemEspPlugin.Log.LogWarning("Checkmarks: Bedarf nicht lesbar - " + ex.Message);
			}
		}

		/// <summary>Liefert {FiR, Nicht-FiR} im Lager, oder null.</summary>
		private static int[] Bestand(string vorlage)
		{
			if (_bestandMethode == null || _bestandFir == null || _bestandNonFir == null)
			{
				return null;
			}
			if (Time.time - _bestandsZeit > 5f)
			{
				_bestandsZeit = Time.time;
				BestandsCache.Clear();
			}
			if (BestandsCache.TryGetValue(vorlage, out var fertig))
			{
				return fertig;
			}
			int[] wert = null;
			try
			{
				object z = _bestandMethode.Invoke(null, new object[] { (MongoID)vorlage });
				if (z != null)
				{
					wert = new int[2]
					{
						(int)_bestandFir.GetValue(z),
						(int)_bestandNonFir.GetValue(z)
					};
				}
			}
			catch
			{
				wert = null;
			}
			BestandsCache[vorlage] = wert;
			return wert;
		}

		/// <summary>
		/// Prueft, ob von dieser Vorlage noch etwas gebraucht wird.
		/// <paramref name="hinweis"/> ist der Text fuers Etikett, etwa
		/// "noch 10 FiR".
		/// </summary>
		public static Lage Pruefen(string vorlage, out string hinweis)
		{
			hinweis = "";
			Suchen();
			if (_tabelle == null || string.IsNullOrEmpty(vorlage))
			{
				return Lage.Unbekannt;
			}
			SchnappschussPflegen();
			if (!Schnappschuss.TryGetValue(vorlage, out var b))
			{
				return Lage.Unbekannt;
			}
			int[] habe = Bestand(vorlage);
			if (habe == null)
			{
				// Ohne Bestand wissen wir nur, dass es ueberhaupt verlangt
				// wird - dann lieber anzeigen als faelschlich verstecken.
				hinweis = BedarfsText(b.Fir, b.NonFir);
				return Lage.NochNoetig;
			}
			// Dieselbe Rechnung wie im Inventar-Hinweis: ueberzaehlige
			// FiR-Stuecke decken auch den Nicht-FiR-Bedarf mit ab.
			int restFir = b.Fir - habe[0];
			int ueberschuss = ((habe[0] > b.Fir) ? (habe[0] - b.Fir) : 0);
			int restNonFir = b.NonFir - habe[1] - ueberschuss;
			if (restFir <= 0 && restNonFir <= 0)
			{
				return Lage.Erledigt;
			}
			hinweis = BedarfsText((restFir > 0) ? restFir : 0, (restNonFir > 0) ? restNonFir : 0);
			return Lage.NochNoetig;
		}

		/// <summary>Zaehlt <see cref="Bestand"/> schon mit, was man am Mann
		/// traegt? Ausserhalb des Raids nimmt AllQuestsCheckmarks das ganze
		/// Inventar, im Raid nur seinen Lager-Schnappschuss - plus das am
		/// Mann, wenn seine Einstellung es so will. Laesst sich das nicht
		/// klaeren, gilt "ja": dann wird nichts doppelt gezaehlt, der
		/// Bestand eher zu klein geschaetzt und lieber angezeigt.</summary>
		private static bool BestandMitAmMann()
		{
			AbstractGame spiel = Singleton<AbstractGame>.Instance;
			if (spiel == null || !spiel.InRaid)
			{
				return true;
			}
			try
			{
				if (_mitRaidSachen?.GetValue(null) is ConfigEntry<bool> schalter)
				{
					return schalter.Value;
				}
			}
			catch
			{
			}
			return true;
		}

		/// <summary>
		/// Restbedarf fuer einen Gegenstand, den eine LAUFENDE Aufgabe
		/// verlangt - aus dem Fortschritt, den das Spiel je Bedingung fuehrt.
		///
		/// So zaehlt das Spiel (Assembly-CSharp, ConditionsConnectorsManager):
		/// Bei HandoverItem ist CurrentValue das bereits Abgegebene. Davon
		/// zieht diese Rechnung den Bestand ab - Lager plus am Mann, bei
		/// FiR-Bedingungen nur FiR-Stuecke, ueberzaehlige FiR decken
		/// Nicht-FiR mit ab. Bei FindItem ist CurrentValue, was im Inventar
		/// liegt (plus schon Abgegebenes der Partner-Abgabe), am Mann also
		/// schon drin. Nur das Lager fehlt im Raid, weil das Spiel es beim
		/// Raidstart aus dem Profil nimmt (<paramref name="lagerUngesehen"/>)
		/// - dann wird das Lager hier abgezogen.
		/// </summary>
		public static Lage PruefenRest(string vorlage, ICollection<Restposten> posten,
			int amMannFir, int amMannNonFir, bool lagerUngesehen, out string hinweis)
		{
			hinweis = "";
			if (posten == null || posten.Count == 0)
			{
				return Lage.Unbekannt;
			}
			Suchen();
			int[] bestand = string.IsNullOrEmpty(vorlage) ? null : Bestand(vorlage);
			// Bestand von AllQuestsCheckmarks in Lager und am Mann aufteilen,
			// damit nichts doppelt zaehlt. Ohne den Mod kennen wir nur das am Mann.
			int lagerFir = 0;
			int lagerNonFir = 0;
			if (bestand != null)
			{
				bool mitAmMann = BestandMitAmMann();
				lagerFir = Math.Max(0, bestand[0] - (mitAmMann ? amMannFir : 0));
				lagerNonFir = Math.Max(0, bestand[1] - (mitAmMann ? amMannNonFir : 0));
			}
			int habeFir = lagerFir + amMannFir;
			int habeNonFir = lagerNonFir + amMannNonFir;

			int abgabeFir = 0;
			int abgabeNonFir = 0;
			int suchFir = 0;
			int suchNonFir = 0;
			bool unbekannt = false;
			foreach (Restposten p in posten)
			{
				if (!p.Bekannt)
				{
					unbekannt = true;
					continue;
				}
				int rest = (int)Math.Ceiling(p.Rest);
				if (rest <= 0)
				{
					continue;
				}
				if (p.Abgabe)
				{
					if (p.Fir)
					{
						abgabeFir += rest;
					}
					else
					{
						abgabeNonFir += rest;
					}
					continue;
				}
				if (lagerUngesehen)
				{
					rest -= p.Fir ? lagerFir : (lagerFir + lagerNonFir);
					if (rest <= 0)
					{
						continue;
					}
				}
				if (p.Fir)
				{
					suchFir += rest;
				}
				else
				{
					suchNonFir += rest;
				}
			}
			int restFir = abgabeFir - habeFir;
			int ueberschuss = ((habeFir > abgabeFir) ? (habeFir - abgabeFir) : 0);
			int restNonFir = abgabeNonFir - habeNonFir - ueberschuss;
			int fir = Math.Max(0, restFir) + suchFir;
			int nonFir = Math.Max(0, restNonFir) + suchNonFir;
			if (unbekannt)
			{
				// Fuer mindestens eine Aufgabe fehlt der Fortschritt - keine
				// Zahl nennen, die nicht stimmt, aber auch nicht ausblenden.
				return Lage.NochNoetig;
			}
			if (fir <= 0 && nonFir <= 0)
			{
				return Lage.Erledigt;
			}
			hinweis = BedarfsText(fir, nonFir);
			return Lage.NochNoetig;
		}

		private static string BedarfsText(int fir, int nonFir)
		{
			if (fir > 0 && nonFir > 0)
			{
				return "noch " + fir + " FiR + " + nonFir;
			}
			if (fir > 0)
			{
				return "noch " + fir + " FiR";
			}
			if (nonFir > 0)
			{
				return "noch " + nonFir;
			}
			return "";
		}

		/// <summary>
		/// Wie viele Stueck dieser Vorlage liegen im Lager (FiR und nicht
		/// zusammen)? -1, wenn sich das nicht feststellen laesst.
		/// </summary>
		public static int ImLager(string vorlage)
		{
			Suchen();
			if (string.IsNullOrEmpty(vorlage))
			{
				return -1;
			}
			int[] habe = Bestand(vorlage);
			return (habe == null) ? (-1) : (habe[0] + habe[1]);
		}

		/// <summary>Dieselbe Rechnung wie <see cref="Pruefen"/>, aber der
		/// Bedarf wird NUR ueber die Aufgaben summiert, die noch ausstehen -
		/// welche das sind, entscheidet der Aufrufer.
		///
		/// WARUM GENAUER: <see cref="Pruefen"/> nimmt die Gesamtsumme aus der
		/// Tabelle. Verlangen zwei Aufgaben denselben Gegenstand und ist eine
		/// davon fuer dich gar nicht mehr offen, ist diese Summe zu hoch -
		/// der Gegenstand gilt dann als "noch gebraucht", obwohl genug da ist.
		/// Der Hinweistext nennt dieselben Zahlen, die auch der Tooltip im
		/// Inventar zeigt.</summary>
		public static Lage PruefenJeAufgabe(string vorlage, Func<string, bool> stehtAus,
			out string hinweis, out string wofuer)
		{
			hinweis = "";
			wofuer = "";
			Suchen();
			// SICHERHEITSHALTER: Ohne die Stueckzahlen je Aufgabe waere jede
			// Summe 0 - und 0 hiesse "Bedarf gedeckt", also KEINE Marke mehr,
			// fuer gar nichts. Aus fehlenden Daten darf nie "ausblenden"
			// folgen; dann lieber die alte Gesamtrechnung.
			if (_mengeCount == null || _questMenge == null)
			{
				return Lage.Unbekannt;
			}
			List<Aufgabenbedarf> aufgaben = AufgabenFuer(vorlage);
			if (aufgaben.Count == 0)
			{
				// Keine Zuordnung lesbar - der Aufrufer nimmt die alte Rechnung.
				return Lage.Unbekannt;
			}

			int noetigFir = 0;
			int noetigNonFir = 0;
			foreach (Aufgabenbedarf a in aufgaben)
			{
				if (stehtAus != null && !stehtAus(a.Id))
				{
					continue;
				}
				if (a.Fir)
				{
					noetigFir += a.Menge;
				}
				else
				{
					noetigNonFir += a.Menge;
				}
				if (string.IsNullOrEmpty(a.Name))
				{
					continue;
				}
				if (wofuer.Length == 0)
				{
					wofuer = a.Name;
				}
				else if (!wofuer.Contains(a.Name))
				{
					wofuer = wofuer + " / " + a.Name;
				}
			}

			if (noetigFir <= 0 && noetigNonFir <= 0)
			{
				// Keine offene Aufgabe verlangt das Stueck mehr.
				return Lage.Erledigt;
			}

			int[] habe = Bestand(vorlage);
			if (habe == null)
			{
				hinweis = BedarfsText(noetigFir, noetigNonFir);
				return Lage.NochNoetig;
			}
			// Ueberzaehlige FiR-Stuecke decken auch den Nicht-FiR-Bedarf ab -
			// dieselbe Regel wie im Inventar-Hinweis.
			int restFir = noetigFir - habe[0];
			int ueberschuss = ((habe[0] > noetigFir) ? (habe[0] - noetigFir) : 0);
			int restNonFir = noetigNonFir - habe[1] - ueberschuss;
			if (restFir <= 0 && restNonFir <= 0)
			{
				return Lage.Erledigt;
			}
			hinweis = BedarfsText((restFir > 0) ? restFir : 0, (restNonFir > 0) ? restNonFir : 0);
			return Lage.NochNoetig;
		}

		/// <summary>Welche Aufgaben verlangen diesen Gegenstand? Liefert je
		/// Aufgabe ihre Kennung und ihren Namen. Leer, wenn die Tabelle die
		/// Zuordnung nicht hergibt - dann muss der Aufrufer ohne sie
		/// auskommen.</summary>
		public static List<Aufgabenbedarf> AufgabenFuer(string vorlage)
		{
			var ergebnis = new List<Aufgabenbedarf>();
			Suchen();
			if (_tabelle == null || _bedarfQuests == null || string.IsNullOrEmpty(vorlage))
			{
				return ergebnis;
			}
			try
			{
				object posten = _tabelle[vorlage];
				if (posten == null)
				{
					return ergebnis;
				}
				IDictionary aufgaben = _bedarfQuests.GetValue(posten) as IDictionary;
				if (aufgaben == null)
				{
					return ergebnis;
				}
				foreach (DictionaryEntry e in aufgaben)
				{
					// Die Kennung ist ein MongoID-Wert - ToString() liefert
					// die Zeichenkette, mit der auch das Auftragsbuch arbeitet.
					string id = (e.Key != null) ? e.Key.ToString() : null;
					if (string.IsNullOrEmpty(id))
					{
						continue;
					}
					string name = null;
					if (_questNameLokal != null)
					{
						name = _questNameLokal.GetValue(e.Value) as string;
					}
					if (string.IsNullOrEmpty(name) && _questName != null)
					{
						name = _questName.GetValue(e.Value) as string;
					}
					int menge = 0;
					bool fir = false;
					if (_questMenge != null)
					{
						object zaehler = _questMenge.GetValue(e.Value);
						if (zaehler != null)
						{
							if (_mengeCount != null)
							{
								menge = Convert.ToInt32(_mengeCount.GetValue(zaehler));
							}
							if (_mengeFir != null)
							{
								fir = Convert.ToBoolean(_mengeFir.GetValue(zaehler));
							}
						}
					}
					ergebnis.Add(new Aufgabenbedarf
					{
						Id = id,
						Name = name ?? "",
						Menge = menge,
						Fir = fir
					});
				}
			}
			catch (Exception ex)
			{
				QuestItemEspPlugin.Log.LogWarning("Checkmarks: Aufgabenzuordnung nicht lesbar - " + ex.Message);
				_bedarfQuests = null;
			}
			return ergebnis;
		}

		/// <summary>Alle Vorlagen, von denen noch etwas gebraucht wird.</summary>
		public static IEnumerable<string> AlleOffenen()
		{
			Suchen();
			if (_tabelle == null)
			{
				return new string[0];
			}
			SchnappschussPflegen();
			return Schnappschuss.Keys;
		}
	}
}
