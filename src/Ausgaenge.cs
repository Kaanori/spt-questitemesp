// ---------------------------------------------------------------
//  Ausgaenge - die Fluchtpunkte dieses Einsatzes als Marken
//
//  Nachgebaut nach dem Mod QuestMarkers von maschine (MIT). Der Kniff
//  daran: Man fragt NICHT alle Ausgaenge der Karte ab, sondern genau
//  die Liste, die das Spiel dir selbst auf der Ausgangsanzeige zeigt -
//  EligiblePoints(Profil) fuer den PMC, die beanspruchten Punkte fuer
//  den Scav. Damit steht nie ein Ausgang im Bild, den du gar nicht
//  nehmen darfst.
//
//  DER ZUFALL WIRD NICHT VERRATEN: Ausgaenge, deren Verfuegbarkeit
//  ausgewuerfelt wird (Chance unter 100), gelten als "nicht bestaetigt"
//  und bekommen die graue Farbe - egal wie der Wurf ausgefallen ist.
//  Sonst waere die Marke ein Spickzettel.
// ---------------------------------------------------------------
using System;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using UnityEngine;

namespace QuestItemESP
{
	public partial class QuestItemEspPlugin
	{
		private enum Ausgangszustand
		{
			Keiner,
			Offen,
			Countdown,
			Gesperrt
		}

		/// <summary>Traegt die Ausgaenge dieses Einsatzes in die Markerliste
		/// ein. Aufgabenbezogene Ausgaenge stehen da schon drin - die
		/// bekommen nur ihren Zustand nachgereicht, statt doppelt zu
		/// erscheinen.</summary>
		private void AusgaengeEintragen(GameWorld welt, Vector3 auge, float hoechstens)
		{
			if (_ausgaenge == null || !_ausgaenge.Value || welt == null)
			{
				return;
			}
			Player ich = welt.MainPlayer;
			if (ich == null || ich.Profile == null)
			{
				return;
			}
			try
			{
				// Bewusst var: der Typ der Ausgangsverwaltung traegt einen
				// Namen, den EFT bei Updates umbenennt.
				var leitung = welt.ExfiltrationController;
				if (leitung == null || leitung.IsMyPlayerBanned())
				{
					return;
				}

				if (ich.Side == EPlayerSide.Savage)
				{
					// Als Scav gibt es nur die Punkte, die dieser Einsatz
					// einem zugeteilt hat.
					ScavExfiltrationPoint[] beansprucht = leitung.ScavExfiltrationPoints;
					if (beansprucht != null)
					{
						for (int i = 0; i < beansprucht.Length; i++)
						{
							ScavExfiltrationPoint p = beansprucht[i];
							if (p != null && p.EligibleIds != null && p.EligibleIds.Contains(ich.ProfileId))
							{
								AusgangEintragen(p, auge, hoechstens);
							}
						}
					}
					EintragenAlle(leitung.GetScavSecretExits(), auge, hoechstens);
					return;
				}

				EintragenAlle(leitung.EligiblePoints(ich.Profile), auge, hoechstens);
				// Geheime Ausgaenge tauchen erst auf, wenn man sie gefunden hat -
				// das entscheidet das Spiel, nicht wir.
				EintragenAlle(leitung.SecretEligiblePoints(), auge, hoechstens);
			}
			catch (Exception ex)
			{
				if (!_ausgaengeGemeldet)
				{
					_ausgaengeGemeldet = true;
					Log.LogWarning("Ausgaenge: " + ex.GetType().Name + " - " + ex.Message);
				}
			}
		}

		private void EintragenAlle(ExfiltrationPoint[] punkte, Vector3 auge, float hoechstens)
		{
			if (punkte == null)
			{
				return;
			}
			for (int i = 0; i < punkte.Length; i++)
			{
				AusgangEintragen(punkte[i], auge, hoechstens);
			}
		}

		private void AusgangEintragen(ExfiltrationPoint punkt, Vector3 auge, float hoechstens)
		{
			if (punkt == null || punkt.Settings == null || string.IsNullOrEmpty(punkt.Settings.Name))
			{
				return;
			}
			Ausgangszustand zustand = ZustandVon(punkt);
			if (zustand == Ausgangszustand.Keiner)
			{
				return;
			}
			string name = punkt.Settings.Name;

			// Verlangt eine Aufgabe diesen Ausgang, steht er schon in der
			// Liste. Dann nur den Zustand nachtragen - zwei Marken am selben
			// Fleck waeren nur Unordnung.
			for (int i = 0; i < _markers.Count; i++)
			{
				if (_markers[i].Kind != MarkerKind.Ausgang || _markers[i].AusgangsName != name)
				{
					continue;
				}
				Marker vorhanden = _markers[i];
				vorhanden.Zustand = zustand;
				_markers[i] = vorhanden;
				return;
			}

			Vector3 ort = AusgangsMitte(punkt);
			float entfernung = Vector3.Distance(auge, ort);
			if (entfernung > hoechstens)
			{
				return;
			}
			_markers.Add(new Marker
			{
				Label = AusgangsText(name),
				Position = ort,
				Distance = entfernung,
				Hoehe = ort.y - auge.y,
				Kind = MarkerKind.Fluchtpunkt,
				Zustand = zustand,
				AusgangsName = name
			});
		}

		/// <summary>Der Anzeigename, wie ihn das Spiel schreibt. Scheitert
		/// die Uebersetzung, bleibt der rohe Name stehen - besser als
		/// nichts.</summary>
		private static string AusgangsText(string name)
		{
			try
			{
				string uebersetzt = name.Localized();
				return string.IsNullOrEmpty(uebersetzt) ? name : uebersetzt;
			}
			catch
			{
				return name;
			}
		}

		/// <summary>Ausgaenge sind grosse Flaechen. Der Ursprungspunkt liegt
		/// oft in einer Ecke davon - die Mitte der Ausloeseflaechen trifft
		/// es viel besser.</summary>
		private static Vector3 AusgangsMitte(ExfiltrationPoint punkt)
		{
			Bounds flaeche = new Bounds(punkt.transform.position, Vector3.zero);
			if (!punkt.gameObject.activeInHierarchy)
			{
				return flaeche.center;
			}
			Collider[] koerper = punkt.GetComponents<Collider>();
			for (int i = 0; i < koerper.Length; i++)
			{
				if (koerper[i] != null && koerper[i].enabled)
				{
					flaeche.Encapsulate(koerper[i].bounds);
				}
			}
			return flaeche.center;
		}

		private static Ausgangszustand ZustandVon(ExfiltrationPoint punkt)
		{
			EExfiltrationStatus stand = punkt.Status;
			if (stand == EExfiltrationStatus.Countdown)
			{
				return Ausgangszustand.Countdown;
			}
			// Ausgewuerfelte Ausgaenge: grau lassen, sonst verriete die Farbe
			// den Wurf.
			if (Ausgewuerfelt(punkt) && punkt.Settings.Chance < 100f)
			{
				return Ausgangszustand.Gesperrt;
			}
			switch (stand)
			{
				case EExfiltrationStatus.RegularMode:
				case EExfiltrationStatus.AwaitsManualActivation:
					// Ein Ausgang, der einen mitspielenden Scav braucht, ist
					// solo nicht zu haben.
					return BrauchtScav(punkt) ? Ausgangszustand.Gesperrt : Ausgangszustand.Offen;
				case EExfiltrationStatus.UncompleteRequirements:
				case EExfiltrationStatus.Pending:
					// Schalter, Auto oder Zug steht noch aus.
					return Ausgangszustand.Gesperrt;
				default:
					return Ausgangszustand.Keiner;
			}
		}

		private static bool Ausgewuerfelt(ExfiltrationPoint punkt)
		{
			return !(punkt is ScavExfiltrationPoint) || punkt is SharedExfiltrationPoint;
		}

		private static bool BrauchtScav(ExfiltrationPoint punkt)
		{
			ExfiltrationRequirement[] noetig = punkt.Requirements;
			if (noetig == null)
			{
				return false;
			}
			for (int i = 0; i < noetig.Length; i++)
			{
				if (noetig[i] != null && noetig[i].Requirement == ERequirementState.ScavCooperation)
				{
					return true;
				}
			}
			return false;
		}
	}
}
