// ---------------------------------------------------------------
//  Anzeigen - wann die Marken ueberhaupt erscheinen duerfen
//
//  Uebernommen aus dem Mod QuestMarkers von maschine (MIT). Bis dahin
//  hat unsere ESP immer gezeichnet, sobald ein Einsatz lief - auch im
//  Inventar, auch nach dem Tod, auch beim Zielen durch ein Zielfernrohr.
//
//  DER WICHTIGSTE FALL IST DAS ZIELFERNROHR: Ein vergroesserndes Visier
//  zeichnet sein Bild mit einer EIGENEN Kamera und einem eigenen
//  Blickwinkel. Unsere Marken werden aber gegen die Hauptkamera
//  gerechnet. Im Linsenbild stehen sie deshalb voellig woanders als
//  der Gegenstand - schlimmer als gar keine Marke, weil man ihnen
//  glaubt. Also: waehrend des Zielens aus.
// ---------------------------------------------------------------
using System;
using Comfort.Common;
using EFT;
using EFT.UI.Screens;

namespace QuestItemESP
{
	public partial class QuestItemEspPlugin
	{
		private bool _gateGemeldet;

		/// <summary>Darf jetzt gezeichnet werden?</summary>
		private bool MarkenErlaubt()
		{
			try
			{
				Player ich = Singleton<GameWorld>.Instantiated
					? Singleton<GameWorld>.Instance.MainPlayer
					: null;
				if (ich == null)
				{
					return false;
				}

				if (_nurImEinsatzbild != null && _nurImEinsatzbild.Value)
				{
					// Aus der Verfolgerperspektive stimmt die Kamera nicht,
					// gegen die wir rechnen.
					if (ich.PointOfView != EPointOfView.FirstPerson)
					{
						return false;
					}
					if (ich.HealthController == null || !ich.HealthController.IsAlive)
					{
						return false;
					}
					// Inventar, Karte, Ausgangsanzeige: dort gehoeren keine
					// Weltmarken hin.
					if (!ImEinsatzbild())
					{
						return false;
					}
				}

				if (_beiOptikAus != null && _beiOptikAus.Value && DurchOptikZielend(ich))
				{
					return false;
				}
			}
			catch (Exception ex)
			{
				if (!_gateGemeldet)
				{
					_gateGemeldet = true;
					Log.LogWarning("Anzeigepruefung: " + ex.GetType().Name + " - " + ex.Message
						+ " (es wird weiter gezeichnet)");
				}
				return true;
			}
			return true;
		}

		private static bool ImEinsatzbild()
		{
			try
			{
				return EftScreenManager.Instance.CheckCurrentScreen(EEftScreenType.BattleUI);
			}
			catch
			{
				// Kennt das Spiel die Abfrage nicht mehr, lieber zeichnen als
				// stumm bleiben.
				return true;
			}
		}

		/// <summary>Zielt der Spieler gerade durch ein VERGROESSERNDES Visier?
		/// Kimme und Korn oder ein Rotpunkt stoeren nicht - die zeichnen
		/// keine eigene Kamera.</summary>
		private static bool DurchOptikZielend(Player ich)
		{
			try
			{
				if (!(ich.HandsController is Player.FirearmController waffe) || !waffe.IsAiming)
				{
					return false;
				}
				var visier = ich.ProceduralWeaponAnimation?.CurrentScope;
				return visier != null && visier.IsOptic;
			}
			catch
			{
				return false;
			}
		}
	}
}
