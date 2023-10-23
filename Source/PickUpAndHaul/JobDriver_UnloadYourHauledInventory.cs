using System.Linq;

namespace PickUpAndHaul;
public class JobDriver_UnloadYourHauledInventory : JobDriver
{
	private int _countToDrop = -1;
	private int _unloadDuration = 3;

	public override void ExposeData()
	{
		base.ExposeData();
		Scribe_Values.Look(ref _countToDrop, "countToDrop", -1);
	}

	public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

	/// <summary>
	/// Find spot, reserve spot, pull thing out of inventory, go to spot, drop stuff, repeat.
	/// </summary>
	/// <returns></returns>
	public override IEnumerable<Toil> MakeNewToils()
	{
		if (ModCompatibilityCheck.ExtendedStorageIsActive)
			_unloadDuration = 20;

		var celebrate = Toils_General.Wait(_unloadDuration);

		var wait = Toils_General.Wait(_unloadDuration);
		yield return wait;

		var carriedThings = pawn.TryGetComp<CompHauledToInventory>().GetHashSet();
		yield return Toil_FindTargetOrDrop(carriedThings);
		yield return Toils_Reserve.Reserve(TargetIndex.B);
		yield return Toil_TryPutFromInventoryIntoHands(carriedThings, wait);

		var carryToCell = Toils_Haul.CarryHauledThingToCell(TargetIndex.B);
		yield return Toils_Jump.JumpIf(carryToCell, () => TargetB.HasThing);
		// if (TargetB.HasThing)
		{
			yield return Toil_VerifyContainerValidOrFindNew();
			var carryToContainer = Toils_Haul.CarryHauledThingToContainer();
			yield return carryToContainer;
			yield return Toils_Haul.DepositHauledThingInContainer(TargetIndex.B, TargetIndex.None);
			yield return Toils_Haul.JumpToCarryToNextContainerIfPossible(carryToContainer, TargetIndex.B);
		}

		var releaseToil = Toil_ReleaseReservation(pawn, job);
		yield return Toils_Jump.Jump(releaseToil);
		// else
		{
			yield return carryToCell;
			yield return Toils_Haul.PlaceHauledThingInCell(TargetIndex.B, carryToCell, true);
		}

		// If the original cell is full, PlaceHauledThingInCell will set a different TargetIndex, resulting in errors on yield return Toils_Reserve.Release.
		// We still gotta release though, mostly because of Extended Storage.
		yield return releaseToil;
		yield return Toils_Jump.Jump(wait);
		yield return celebrate;
	}

	private Toil Toil_FindTargetOrDrop(HashSet<Thing> carriedThings)
		=> new()
		{
			initAction = () =>
			{
				var unloadableThing = FirstUnloadableThing(pawn, carriedThings);

				if (unloadableThing.Count == 0 && carriedThings.Count == 0)
					EndJobWith(JobCondition.Succeeded);

				if (unloadableThing.Count != 0)
				{
					//StoragePriority currentPriority = StoreUtility.StoragePriorityAtFor(pawn.Position, unloadableThing.Thing);
					if (!StoreUtility.TryFindStoreCellNearColonyDesperate(unloadableThing.Thing, pawn, out var c))
					{
						pawn.inventory.innerContainer.TryDrop(unloadableThing.Thing, ThingPlaceMode.Near, unloadableThing.Thing.stackCount, out var _);
						EndJobWith(JobCondition.Succeeded);
					}
					else
					{
						job.SetTarget(TargetIndex.A, unloadableThing.Thing);
						job.SetTarget(TargetIndex.B, c);
						_countToDrop = unloadableThing.Thing.stackCount;
					}
				}
			}
		};

	private Toil Toil_TryPutFromInventoryIntoHands(HashSet<Thing> carriedThings, Toil wait)
		=> new()
		{
			initAction = () =>
			{
				var thing = job.GetTarget(TargetIndex.A).Thing;
				if (thing == null || !pawn.inventory.innerContainer.Contains(thing))
				{
					carriedThings.Remove(thing);
					pawn.jobs.curDriver.JumpToToil(wait);
					return;
				}
				if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) || !thing.def.EverStorable(false))
				{
					pawn.inventory.innerContainer.TryDrop(thing, ThingPlaceMode.Near, _countToDrop, out thing);
					EndJobWith(JobCondition.Succeeded);
					carriedThings.Remove(thing);
				}
				else
				{
					pawn.inventory.innerContainer.TryTransferToContainer(thing, pawn.carryTracker.innerContainer, _countToDrop, out thing);
					job.count = _countToDrop;
					job.SetTarget(TargetIndex.A, thing);
					carriedThings.Remove(thing);
				}

				if (ModCompatibilityCheck.CombatExtendedIsActive)
					CompatHelper.UpdateInventory(pawn);

				thing.SetForbidden(false, false);
			}
		};

	private static Toil Toil_ReleaseReservation(Pawn pawn, Job job)
		=> new()
		{
			initAction = () =>
			{
				if (pawn.Map.reservationManager.ReservedBy(job.targetB, pawn, pawn.CurJob)
				 && !ModCompatibilityCheck.HCSKIsActive)
				{
					pawn.Map.reservationManager.Release(job.targetB, pawn, pawn.CurJob);
				}
			}
		};

	private Toil Toil_VerifyContainerValidOrFindNew()
		=> new()
		{
			initAction = () =>
			{
				if (!IsDestinationValidContainer())
				{
					var thing = TargetA.Thing;
					if (StoreUtility.TryFindBestBetterNonSlotGroupStorageFor(
						  thing, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing),
							 pawn.Faction, out var haulDestination, true))
					{
						var destinationAsThing = haulDestination as Thing;
						if (destinationAsThing.TryGetInnerInteractableThingOwner() != null)
							job.SetTarget(TargetIndex.B, destinationAsThing);
					}
					else
					{
						pawn.carryTracker.innerContainer.TryDrop(thing, ThingPlaceMode.Near, thing.stackCount, out var _);
						EndJobWith(JobCondition.Succeeded);
					}
				}
			}
		};

	private bool IsDestinationValidContainer()
		=> TargetB.Thing.TryGetInnerInteractableThingOwner() is { } thingOwner
		&& TargetA.Thing is var thing
		&& thingOwner.CanAcceptAnyOf(thing, true)
		&& TargetB.Thing is IHaulDestination haulDestination
		&& haulDestination.Accepts(thing);

	private static ThingCount FirstUnloadableThing(Pawn pawn, HashSet<Thing> carriedThings)
	{
		var innerPawnContainer = pawn.inventory.innerContainer;

		foreach (var thing in carriedThings.OrderBy(t => t.def.FirstThingCategory?.index).ThenBy(x => x.def.defName))
		{
			//find the overlap.
			if (!innerPawnContainer.Contains(thing))
			{
				//merged partially picked up stacks get a different thingID in inventory
				var stragglerDef = thing.def;
				carriedThings.Remove(thing);

				//we have no method of grabbing the newly generated thingID. This is the solution to that.
				for (var i = 0; i < innerPawnContainer.Count; i++)
				{
					var dirtyStraggler = innerPawnContainer[i];
					if (dirtyStraggler.def == stragglerDef)
						return new(dirtyStraggler, dirtyStraggler.stackCount);
				}
			}
			return new(thing, thing.stackCount);
		}
		return default;
	}
}