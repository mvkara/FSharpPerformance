﻿module TopologicalSort.Version11
// Yeah, we're using pointers :)
#nowarn "9"

(*
Version 11:
Tracking the count of remaining inbound Edges instead of checking
whether all of the incoming Edges have been removed.
*)

open System
open System.Collections.Generic
open FSharp.NativeInterop
open Collections

     
let inline stackalloc<'a when 'a: unmanaged> (length: int): Span<'a> =
  let p = NativePtr.stackalloc<'a> length |> NativePtr.toVoidPtr
  Span<'a>(p, length)
        

[<RequireQualifiedAccess>]
module private Units =

    [<Measure>] type Node
    [<Measure>] type Edge
    [<Measure>] type Index


type Index = int<Units.Index>

module Index =
        
    let inline create (i: int) =
        if i < 0 then
            invalidArg (nameof i) "Cannot have an Index less than 0"
            
        LanguagePrimitives.Int32WithMeasure<Units.Index> i


type Node = int<Units.Node>

module Node =
    
    let inline create (i: int) =
        if i < 0 then
            invalidArg (nameof i) "Cannot have a Node less than 0"
            
        LanguagePrimitives.Int32WithMeasure<Units.Node> i


type Edge = int64<Units.Edge>

module Edge =

    let inline create (source: Node) (target: Node) =
        (((int64 source) <<< 32) ||| (int64 target))
        |> LanguagePrimitives.Int64WithMeasure<Units.Edge>
        
    let inline getSource (edge: Edge) =
        ((int64 edge) >>> 32)
        |> int
        |> LanguagePrimitives.Int32WithMeasure<Units.Node>

    let inline getTarget (edge: Edge) =
        int edge
        |> LanguagePrimitives.Int32WithMeasure<Units.Node>
        

type EdgeTracker (nodeCount: int) =
    let bitsRequired = ((nodeCount * nodeCount) + 63) / 64
    let values = Array.create bitsRequired 0UL
    
    // Public for the purposes of inlining
    member b.NodeCount = nodeCount
    member b.Values = values
    
    member inline b.Add (edge: Edge) =
        let source = Edge.getSource edge
        let target = Edge.getTarget edge
        let location = (int source) * b.NodeCount + (int target)
        let bucket = location >>> 6
        let offset = location &&& 0x3F
        let mask = 1UL <<< offset
        b.Values[bucket] <- b.Values[bucket] ||| mask
        
    member inline b.Remove (edge: Edge) =
        let source = Edge.getSource edge
        let target = Edge.getTarget edge
        let location = (int source) * b.NodeCount + (int target)
        let bucket = location >>> 6
        let offset = location &&& 0x3F
        let mask = 1UL <<< offset
        b.Values[bucket] <- b.Values[bucket] &&& ~~~mask

    member inline b.Contains (edge: Edge) =
        let source = Edge.getSource edge
        let target = Edge.getTarget edge
        let location = (int source) * b.NodeCount + (int target)
        let bucket = location >>> 6
        let offset = location &&& 0x3F
        ((b.Values[bucket] >>> offset) &&& 1UL) = 1UL

    member b.Clear () =
        for i = 0 to b.Values.Length - 1 do
            b.Values[i] <- 0UL

    member b.Count =
        let mutable count = 0
        
        for i = 0 to b.Values.Length - 1 do
            count <- count + (System.Numerics.BitOperations.PopCount b.Values[i])

        count

[<Struct>]
type Range =
    {
        Start : Index
        Length : Index
    }
    static member Zero =
        {
            Start = Index.create 0
            Length = Index.create 0
        }
    
module Range =
    
    let create start length =
        {
            Start = start
            Length = length
        }
    
    [<CompiledName("Iterate")>]
    let inline iter ([<InlineIfLambda>] f: Index -> unit) (range: Range) =
        let mutable i = range.Start
        let bound = range.Start + range.Length

        while i < bound do
            f i
            i <- i + LanguagePrimitives.Int32WithMeasure<Units.Index> 1
            
            
    let inline forall ([<InlineIfLambda>] f: Index -> bool) (range: Range) =
        let mutable result = true
        let mutable i = range.Start
        let bound = range.Start + range.Length

        while i < bound && result do
            result <- f i
            i <- i + LanguagePrimitives.Int32WithMeasure<Units.Index> 1
        
        result
            
    

type SourceRanges = Bar<Units.Node, Range>
type SourceEdges = Bar<Units.Index, Edge>
type TargetRanges = Bar<Units.Node, Range>
type TargetEdges = Bar<Units.Index, Edge>


[<Struct>]
type Graph = {
    SourceRanges : SourceRanges
    SourceEdges : SourceEdges
    TargetRanges : TargetRanges
    TargetEdges : TargetEdges
}
    
module Graph =
    
    let private getNodeCount (edges: Edge[]) =
        let nodes = HashSet()
        
        for edge in edges do
            let source = Edge.getSource edge
            let target = Edge.getTarget edge
            nodes.Add source |> ignore
            nodes.Add target |> ignore
            
        LanguagePrimitives.Int32WithMeasure<Units.Node> nodes.Count
    
    let private createSourcesAndTargets (nodeCount: int<Units.Node>) (edges: Edge[]) =
        let mutable sourcesAcc = Row.create nodeCount []
        let mutable targetsAcc = Row.create nodeCount []
        
        for edge in edges do
            let source = Edge.getSource edge
            let target = Edge.getTarget edge
            
            sourcesAcc[target] <- edge :: sourcesAcc[target]
            targetsAcc[source] <- edge :: targetsAcc[source]
            
        let finalSources =
            sourcesAcc
            |> Row.map Array.ofList
            
        let finalTargets =
            targetsAcc
            |> Row.map Array.ofList
            
        finalSources.Bar, finalTargets.Bar

        
    let private createIndexesAndValues (nodeData: Bar<'Measure, Edge[]>) =
        let mutable ranges = Row.create nodeData.Length Range.Zero
        let mutable nextStartIndex = Index.create 0
        
        nodeData
        |> Bar.iteri (fun nodeId nodes ->
            let length =
                nodes.Length
                |> int
                |> Index.create
            let newRange = Range.create nextStartIndex length
            ranges[nodeId] <- newRange
            nextStartIndex <- nextStartIndex + length
            )
        
        let values =
            nodeData._values
            |> Array.concat
            |> Bar<Units.Index, _>
        
        ranges.Bar, values
        
        
    let create (edges: Edge[]) =
        let nodeCount = getNodeCount edges
        let nodeSources, nodeTargets = createSourcesAndTargets nodeCount edges
        
        let sourceRanges, sourceNodes = createIndexesAndValues nodeSources
        let targetRanges, targetNodes = createIndexesAndValues nodeTargets
        
        {
            SourceRanges = sourceRanges
            SourceEdges = sourceNodes
            TargetRanges = targetRanges
            TargetEdges = targetNodes
        }        


let sort (graph: Graph) =
    
    let sourceRanges = graph.SourceRanges
    let targetRanges = graph.TargetRanges
    let targetEdges = graph.TargetEdges
    
    let result = GC.AllocateUninitializedArray (int sourceRanges.Length)
    let mutable nextToProcessIdx = 0
    let mutable resultCount = 0
    
    
    let sourceCounts = stackalloc<uint> (int targetRanges.Length)
    let mutable nodeId = 0<Units.Node>
    
    // This is necessary due to the Span not being capture in a lambda
    while nodeId < sourceRanges.Length do
        sourceCounts[int nodeId] <- uint sourceRanges[nodeId].Length
        result[resultCount] <- nodeId
        if sourceCounts[int nodeId] = 0u then
            result[resultCount] <- nodeId
            resultCount <- resultCount + 1
        nodeId <- nodeId + 1<_>

    
    while nextToProcessIdx < resultCount do

        let targetRange = targetRanges[result[nextToProcessIdx]]
        let mutable targetIndex = targetRange.Start
        let bound = targetRange.Start + targetRange.Length
        while targetIndex < bound do
            let targetNodeId = Edge.getTarget targetEdges[targetIndex]
            sourceCounts[int targetNodeId] <- sourceCounts[int targetNodeId] - 1u
            
            if sourceCounts[int targetNodeId] = 0u then
                result[resultCount] <- targetNodeId
                resultCount <- resultCount + 1

            targetIndex <- targetIndex + 1<_>
        
        nextToProcessIdx <- nextToProcessIdx + 1


    if resultCount < result.Length then
        None
    else
        Some result
