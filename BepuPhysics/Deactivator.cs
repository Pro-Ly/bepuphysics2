﻿using BepuUtilities.Collections;
using BepuUtilities.Memory;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace BepuPhysics
{
    public class Deactivator
    {
        IdPool<Buffer<int>> islandIdPool;
        Bodies bodies;
        Solver solver;
        BufferPool pool;
        public int InitialIslandBodyCapacity = 1024;
        public int InitialIslandConstraintCapacity = 1024;
        public Deactivator(Bodies bodies, Solver solver, BufferPool pool)
        {
            this.bodies = bodies;
            this.solver = solver;
            this.pool = pool;
            IdPool<Buffer<int>>.Create(pool.SpecializeFor<int>(), 16, out islandIdPool);
        }

        struct ConstraintBodyEnumerator : IForEach<int>
        {
            public QuickList<int, Buffer<int>> ConstraintBodyIndices;
            public BufferPool<int> IntPool;
            public int SourceIndex;
            public void LoopBody(int bodyIndex)
            {
                if (bodyIndex != SourceIndex)
                {
                    ConstraintBodyIndices.Add(bodyIndex, IntPool);
                }
            }
        }



        struct ForcedDeactivationPredicate : IPredicate<int>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Matches(ref int bodyIndex)
            {
                return true;
            }
        }
        struct DeactivationPredicate : IPredicate<int>
        {
            public Bodies Bodies;
            public HandleSet PreviouslyTraversedBodies;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Matches(ref int bodyIndex)
            {
                //Note that we block traversals on a single thread from retreading old ground.
                if (PreviouslyTraversedBodies.Contains(bodyIndex))
                    return false;
                PreviouslyTraversedBodies.AddUnsafely(bodyIndex);
                return Bodies.ActiveSet.Activity[bodyIndex].DeactivationCandidate;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool PushBody<TTraversalPredicate>(int bodyIndex, ref HandleSet consideredBodies, ref QuickList<int, Buffer<int>> bodyIndices, ref QuickList<int, Buffer<int>> visitationStack,
            ref BufferPool<int> intPool, ref TTraversalPredicate predicate) where TTraversalPredicate : IPredicate<int>
        {
            if (predicate.Matches(ref bodyIndex))
            {
                if (!consideredBodies.Contains(bodyIndex))
                {
                    //This body has not yet been traversed. Push it onto the stack.
                    bodyIndices.Add(bodyIndex, intPool);
                    consideredBodies.AddUnsafely(bodyIndex);
                    visitationStack.Add(bodyIndex, intPool);

                }
                return true;
            }
            return false;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool EnqueueUnvisitedNeighbors<TTraversalPredicate>(int bodyHandle,
            ref QuickList<int, Buffer<int>> bodyHandles,
            ref QuickList<int, Buffer<int>> constraintHandles,
            ref HandleSet consideredBodies, ref HandleSet consideredConstraints,
            ref QuickList<int, Buffer<int>> visitationStack,
            ref ConstraintBodyEnumerator bodyEnumerator,
            ref BufferPool<int> intPool, ref TTraversalPredicate predicate) where TTraversalPredicate : IPredicate<int>
        {
            var bodyIndex = bodies.HandleToLocation[bodyHandle].Index;
            bodyEnumerator.SourceIndex = bodyIndex;
            ref var list = ref bodies.ActiveSet.Constraints[bodyIndex];
            for (int i = 0; i < list.Count; ++i)
            {
                ref var entry = ref list[i];
                if (!consideredConstraints.Contains(entry.ConnectingConstraintHandle))
                {
                    //This constraint has not yet been traversed. Follow the constraint to every other connected body.
                    constraintHandles.Add(entry.ConnectingConstraintHandle, intPool);
                    consideredConstraints.AddUnsafely(entry.ConnectingConstraintHandle);
                    solver.EnumerateConnectedBodies(entry.ConnectingConstraintHandle, ref bodyEnumerator);
                    for (int j = 0; j < bodyEnumerator.ConstraintBodyIndices.Count; ++j)
                    {
                        var connectedBodyIndex = bodyEnumerator.ConstraintBodyIndices[j];
                        if (!PushBody(connectedBodyIndex, ref consideredBodies, ref bodyHandles, ref visitationStack, ref intPool, ref predicate))
                            return false;
                    }
                    bodyEnumerator.ConstraintBodyIndices.Count = 0;
                }
            }
            return true;
        }

        void CleanUpTraversal(
            BufferPool pool,
            ref HandleSet consideredBodies, ref HandleSet consideredConstraints,
            ref QuickList<int, Buffer<int>> visitationStack)
        {
            var intPool = pool.SpecializeFor<int>();
            consideredBodies.Dispose(pool);
            consideredConstraints.Dispose(pool);
            visitationStack.Dispose(intPool);
        }

        /// <summary>
        /// Traverses the active constraint graph collecting bodies that match a predicate. If any body visited during the traversal fails to match the predicate, the traversal terminates.
        /// </summary>
        /// <typeparam name="TTraversalPredicate">Type of the predicate to test each body index with.</typeparam>
        /// <param name="pool">Pool to allocate temporary collections from.</param>
        /// <param name="startingActiveBodyIndex">Index of the active body to start the traversal at.</param>
        /// <param name="predicate">Predicate to test each traversed body with. If any body results in the predicate returning false, the traversal stops and the function returns false.</param>
        /// <param name="bodyIndices">List to fill with body indices traversed during island collection. Bodies failing the predicate will not be included.</param>
        /// <param name="constraintHandles">List to fill with constraint handles traversed during island collection.</param>
        /// <returns>True if the simulation graph was traversed without ever finding a body that made the predicate return false. False if any body failed the predicate.
        /// The bodyIndices and constraintHandles lists will contain all traversed predicate-passing bodies and constraints.</returns>
        public bool CollectIsland<TTraversalPredicate>(BufferPool pool, int startingActiveBodyIndex, ref TTraversalPredicate predicate,
            ref QuickList<int, Buffer<int>> bodyIndices, ref QuickList<int, Buffer<int>> constraintHandles) where TTraversalPredicate : IPredicate<int>
        {
            //We'll build the island by working depth-first. This means the bodies and constraints we accumulate will be stored in any inactive island by depth-first order,
            //which happens to be a pretty decent layout for cache purposes. In other words, when we wake these islands back up, bodies near each other in the graph will have 
            //a higher chance of being near each other in memory. Bodies directly connected may often end up adjacent to each other, meaning loading one body may give you the other for 'free'
            //(assuming they share a cache line).
            //The DFS order for constraints is not quite as helpful as the constraint optimizer's sort, but it's not terrible.

            //Despite being DFS, there is no guarantee that the visitation stack will be any smaller than the final island itself, and we have no way of knowing how big the island is 
            //ahead of time- except that it can't be larger than the entire active simulation.
            var intPool = pool.SpecializeFor<int>();
            var initialBodyCapacity = Math.Min(InitialIslandBodyCapacity, bodies.ActiveSet.Count);
            //Note that we track all considered bodies AND constraints. 
            //While we only need to track one of them for the purposes of traversal, tracking both allows low-overhead collection of unique bodies and constraints.
            //Note that the constraint handle set is initialized to cover the entire handle span. 
            //That's actually fine- every single object occupies only a single bit, so 131072 objects only use 16KB.
            var consideredBodies = new HandleSet(pool, bodies.ActiveSet.Count);
            var consideredConstraints = new HandleSet(pool, solver.HandlePool.HighestPossiblyClaimedId + 1);
            //The stack will store body indices.
            QuickList<int, Buffer<int>>.Create(intPool, initialBodyCapacity, out var visitationStack);

            //Start the traversal by pushing the initial body conditionally.
            if (!PushBody(startingActiveBodyIndex, ref consideredBodies, ref bodyIndices, ref visitationStack, ref intPool, ref predicate))
            {
                CleanUpTraversal(pool, ref consideredBodies, ref consideredConstraints, ref visitationStack);
                return false;
            }
            var enumerator = new ConstraintBodyEnumerator();
            enumerator.IntPool = intPool;

            while (visitationStack.TryPop(out var nextIndexToVisit))
            {
                QuickList<int, Buffer<int>>.Create(intPool, 4, out enumerator.ConstraintBodyIndices);
                if (!EnqueueUnvisitedNeighbors(nextIndexToVisit, ref bodyIndices, ref constraintHandles, ref consideredBodies, ref consideredConstraints, ref visitationStack,
                    ref enumerator, ref intPool, ref predicate))
                {
                    CleanUpTraversal(pool, ref consideredBodies, ref consideredConstraints, ref visitationStack);
                    return false;
                }
            }
            //The visitation stack was emptied without finding any traversal disqualifying bodies.
            return true;
        }

        struct Job
        {
            public QuickList<int, Buffer<int>> TargetBodyIndices;
            public int TargetTraversedBodyCount;
            public int TargetDeactivatedBodyCount;
        }



        void Collect(int workerIndex, BufferPool threadPool, ref Job job)
        {
            var initialBodyCapacity = Math.Min(InitialIslandBodyCapacity, bodies.ActiveSet.Count);
            var intPool = pool.SpecializeFor<int>();
            QuickList<int, Buffer<int>>.Create(intPool, initialBodyCapacity, out var bodyIndices);
            QuickList<int, Buffer<int>>.Create(intPool, Math.Min(InitialIslandBodyCapacity, solver.HandlePool.HighestPossiblyClaimedId + 1), out var constraintHandles);

            DeactivationPredicate predicate;
            predicate.Bodies = bodies;
            predicate.PreviouslyTraversedBodies = new HandleSet(threadPool, bodies.ActiveSet.Count);
            var traversedBodies = 0;
            var deactivatedBodies = 0;
            int targetIndex = 0;
            
            while (traversedBodies < job.TargetTraversedBodyCount && deactivatedBodies < job.TargetDeactivatedBodyCount && targetIndex < job.TargetBodyIndices.Count)
            {
                //This thread still has some deactivation budget, so do another traversal.
                var bodyIndex = job.TargetBodyIndices[targetIndex++];
                if (CollectIsland(threadPool, bodyIndex, ref predicate, ref bodyIndices, ref constraintHandles))
                {
                    //Found an island to deactivate!
                    traversedBodies += bodyIndices.Count;
                    deactivatedBodies += bodyIndices.Count;

                    //Note that the deactivation predicate refuses to visit any body that was visited in any previous traversal on this thread. 
                    //From that we know that any newly discovered island is unique *on this thread*. It's very possible that a different thread has found the same
                    //island, but we let that happen in favor of avoiding tons of sync overhead.
                    //When the main thread is actually applying deactivations sequentially, it will be working with body handles- it can look up
                    //the current location of a body. If it's already been deactivated, the island has already been handled and is ignored.

                    //TODO: CREATE PROTO-ISLAND 
                    bodyIndices.Count = 0;
                    constraintHandles.Count = 0;
                }
                else
                {
                    //This island failed the predicate, so it can't deactivate. But it did cost something.
                    traversedBodies += bodyIndices.Count;
                }
            }
            predicate.PreviouslyTraversedBodies.Dispose(threadPool);
            bodyIndices.Dispose(intPool);
            constraintHandles.Dispose(intPool);
        }

        //public void Create(ref QuickList<int, Buffer<int>> bodyHandles, ref QuickList<int, Buffer<int>> constraintHandles,
        //    BufferPool mainPool, BufferPool threadPool)
        //{
        //    //Note that, while we did encounter the indices associated with the island bodies handles and *could* have cached them, we opted to store the handles instead.
        //    //This does incur additional (warm) indirections, but we would like to also use the handles again- to remove from the active set.
        //    //Creating this island does not modify anything about the existing active set. All of that is deferred.

        //    //Note that, while we have already traversed the constraint's connected bodies to collect the island, we did not cache all required data during the traversal.
        //    //Doing so would be *usually* wasteful- the vast majority of traversals result in no deactivation.
        //    //Further, the traversal does not otherwise need to touch the prestep data and accumulated impulses. Those are quite large, so avoiding needless accesses
        //    //are important for keeping the traversal reasonably speedy.
        //    //Given that we have to grab that additional information anyway, and given that it is likely in L1 (or failing that, L2) cache, we re-enumerate the constraint body 
        //    //handles here.

        //    //We have a bit of an annoyance to deal with:
        //    //1) By convention, we never hold information in per-thread buffer pools between frames.
        //    //2) We'd like to be able to run island creation on multiple threads.
        //    //3) Island creation requires allocating space for all the body and constraint data.
        //    //Implication:
        //    //We must synchronize access to the main pool when retrieving persisted buffers. All ephemeral data comes from the thread pool.
        //    //While this isn't too problematic (time spent retrieving island resources is going to be extremely brief), 
        //    //the main pool access does restrict job scheduling with other main pool users that are unaware of the synchronization requirement.

        //    //Unless we perform constraint batching during traversal, the numbers of constraint batches, type batches, and constraints within individual type batches are unknown.
        //    //We cannot just lock once and allocate a minimally sufficient set of buffers.
        //    //An option:
        //    //1) Enumerate each constraint's bodies. Convert them to handles and perform batching, locally creating constraintbatches and type batches, but only fill the body references.
        //    //2) As you go, store the new handle->island location mapping.
        //    //3) Using the capacities detected by 

        //    var batchReferencedHandlesPool = threadPool.SpecializeFor<HandleSet>();
        //    var intPool = threadPool.SpecializeFor<int>();
        //    constraintHandles.
        //    batchReferencedHandlesPool.Take(16, out var batchReferencedHandles);

        //    for (int i = 0; i < ConstraintBatches.Count; ++i)
        //    {
        //        batchReferencedHandles[i].Dispose(threadPool);
        //    }
        //    batchReferencedHandlesPool.Return(ref batchReferencedHandles);

        //}


        public void Dispose()
        {
            islandIdPool.Dispose(pool.SpecializeFor<int>());
        }
    }
}
