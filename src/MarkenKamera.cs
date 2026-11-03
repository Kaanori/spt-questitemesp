// ---------------------------------------------------------------
//  Marken nachzeichnen - der Weg, der ohne eigenen Layer auskommt
//
//  VORGESCHICHTE, ZWEI SACKGASSEN:
//
//   1. Tiefentest am Material. Nachweislich gesetzt (Log: Punkt
//      Wert 8, Schrift Wert 0 - beides "nicht pruefen"), und die
//      Marken blieben trotzdem verdeckt. Es scheiterte also nicht
//      am Tiefentest, sondern daran, dass die Marken gar nicht
//      erst gezeichnet wurden.
//
//   2. Eigene Kamera auf eigenem Layer. Gescheitert, weil Tarkov
//      ALLE 32 Layer belegt - es gibt keinen freien, und ein
//      fremder haette die Objekte anderer Systeme mitgezeichnet.
//
//  DIESER WEG: ein CommandBuffer an der Spielkamera, eingehaengt
//  bei "AfterEverything" - also nachdem das Bild vollstaendig
//  fertig ist. Darin steht fuer jede Marke ein DrawRenderer.
//
//  WARUM DAS WIRKT, wo alles andere versagt hat: DrawRenderer
//  zeichnet den Renderer BEDINGUNGSLOS. Es fragt nicht, ob etwas
//  davor liegt, ob die Kamera das Objekt in ihrer Sichtprüfung
//  hatte oder ob irgendein Aussortieren es verworfen hat. Damit
//  sind beide moeglichen Ursachen zugleich erledigt: wurde die
//  Marke vorher aussortiert, wird sie jetzt trotzdem gezeichnet;
//  wurde sie uebermalt, kommt sie jetzt zuletzt dran.
//
//  Kein Layer, keine zweite Kamera, nichts am Spiel veraendert -
//  der Puffer wird beim Beenden einfach wieder abgehaengt.
//
//  Abschaltbar unter "4. Weltmarken" -> "Marken nachzeichnen".
// ---------------------------------------------------------------
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace QuestItemESP
{
	public partial class QuestItemEspPlugin
	{
		private ConfigEntry<bool> _nachzeichnen;

		// WANN der Puffer laeuft. Bis 22.09.2026 stand hier AfterEverything -
		// also NACH Nachbearbeitung und Hochskalierung. Zu dem Zeitpunkt hat
		// Tarkov die Kamerawerte schon fuers Hochskalieren umgestellt: jede
		// Marke erschien ein zweites Mal, verkleinert und nach unten
		// verschoben, und rueckte beim Naeherkommen ans Original heran.
		// BeforeImageEffects ist nach allem Durchsichtigen, aber noch in der
		// Aufloesung und Projektion der Szene - die Nachzeichnung liegt damit
		// deckungsgleich auf der normalen Zeichnung.
		private const CameraEvent PufferZeitpunkt = CameraEvent.BeforeImageEffects;

		private CommandBuffer _markenPuffer;

		// An welcher Kamera der Puffer gerade haengt. Die Spielkamera kann
		// wechseln (Menue, Raid, Zielfernrohr), darum wird das mitgefuehrt.
		private Camera _pufferKamera;

		private bool _pufferGemeldet;

		private bool _schriftPufferGemeldet;

		private void KameraEinstellungenAnlegen()
		{
			_nachzeichnen = base.Config.Bind("4. Weltmarken", "Marken nachzeichnen", defaultValue: true,
				"An: die Marken werden am Ende jedes Bildes noch einmal gezeichnet,\n"
				+ "unabhaengig davon, was das Spiel davor aussortiert oder uebermalt\n"
				+ "hat. Das ist der zuverlaessige Weg, sie durch Waende zu sehen.\n"
				+ "Aus: nur der Tiefentest der Materialien - reicht in Tarkov nicht.");
		}

		/// <summary>Haengt den Puffer an die gerade benutzte Kamera, und
		/// haengt ihn von der vorherigen ab.</summary>
		private void PufferAnKamera(Camera haupt)
		{
			if (_markenPuffer == null)
			{
				_markenPuffer = new CommandBuffer();
				_markenPuffer.name = "QuestItemESP-Marken";
			}
			if ((Object)_pufferKamera == (Object)haupt)
			{
				return;
			}
			if (_pufferKamera != null)
			{
				_pufferKamera.RemoveCommandBuffer(PufferZeitpunkt, _markenPuffer);
			}
			_pufferKamera = haupt;
			if (_pufferKamera != null)
			{
				_pufferKamera.AddCommandBuffer(PufferZeitpunkt, _markenPuffer);
				if (!_pufferGemeldet)
				{
					_pufferGemeldet = true;
					Log.LogInfo("Weltmarken: Nachzeichnen aktiv an Kamera \"" + _pufferKamera.name + "\".");
				}
			}
		}

		private void PufferAbhaengen()
		{
			if (_pufferKamera != null && _markenPuffer != null)
			{
				_pufferKamera.RemoveCommandBuffer(PufferZeitpunkt, _markenPuffer);
			}
			_pufferKamera = null;
			if (_markenPuffer != null)
			{
				_markenPuffer.Clear();
			}
		}

		/// <summary>Fuellt den Puffer neu - einmal pro Bild, weil sich
		/// Anzahl und Zustand der Marken staendig aendern.</summary>
		private void MarkenKameraAbgleichen(Camera haupt)
		{
			bool gewuenscht = (_nachzeichnen != null && _nachzeichnen.Value
				&& _weltmarken != null && _weltmarken.Value
				&& haupt != null);

			if (!gewuenscht)
			{
				PufferAbhaengen();
				return;
			}

			PufferAnKamera(haupt);
			if (_markenPuffer == null)
			{
				return;
			}

			_markenPuffer.Clear();
			// Sicherheitshalber die Kamerawerte selbst setzen, statt sich
			// darauf zu verlassen, was zu diesem Zeitpunkt gerade gilt.
			_markenPuffer.SetViewProjectionMatrices(haupt.worldToCameraMatrix, haupt.projectionMatrix);

			for (int i = 0; i < _weltVorrat.Count; i++)
			{
				WeltMarke m = _weltVorrat[i];
				if (m.Objekt == null || !m.Objekt.activeSelf)
				{
					continue;
				}
				if (m.Symbol != null && m.Symbol.enabled)
				{
					_markenPuffer.DrawRenderer(m.Symbol, m.Symbol.sharedMaterial);
				}
				if (m.Strich != null && m.Strich.enabled)
				{
					_markenPuffer.DrawRenderer(m.Strich, m.Strich.sharedMaterial);
				}
				if (m.Text != null && m.Text.enabled && m.TextZeichner != null)
				{
					// Die Schrift braucht mehr Sorgfalt als die Sprites: der
					// TextMeshPro-Shader kann mehrere Durchgaenge haben (Kontur,
					// Schatten, Flaeche). DrawRenderer zeichnet ohne Angabe nur
					// den ERSTEN - und wenn der leer ist, sieht man nichts.
					// Darum alle Durchgaenge einzeln anstossen.
					Material stoff = m.Text.fontMaterial;
					if (stoff != null)
					{
						int paesse = Mathf.Max(1, stoff.passCount);
						for (int pass = 0; pass < paesse; pass++)
						{
							_markenPuffer.DrawRenderer(m.TextZeichner, stoff, 0, pass);
						}

						if (!_schriftPufferGemeldet)
						{
							_schriftPufferGemeldet = true;
							Mesh netz = null;
							MeshFilter filter = m.TextZeichner.GetComponent<MeshFilter>();
							if (filter != null)
							{
								netz = filter.sharedMesh;
							}
							Log.LogInfo("Weltmarken: Schrift nachzeichnen - Renderer aktiv="
								+ m.TextZeichner.enabled
								+ ", Durchgaenge=" + paesse
								+ ", Eckpunkte=" + ((netz != null) ? netz.vertexCount : -1)
								+ ", Shader=\"" + ((stoff.shader != null) ? stoff.shader.name : "?") + "\"");
						}
					}
				}
			}
		}

		private void MarkenKameraAufraeumen()
		{
			PufferAbhaengen();
			if (_markenPuffer != null)
			{
				_markenPuffer.Release();
				_markenPuffer = null;
			}
		}

		/// <summary>Bleibt als leerer Haken bestehen: einen eigenen Layer
		/// gibt es in Tarkov nicht, alle 32 sind belegt. Die Marken bleiben
		/// darum auf dem Standard-Layer.</summary>
		private void InMarkenLayer(GameObject o)
		{
		}
	}
}
