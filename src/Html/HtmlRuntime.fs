﻿namespace FSharp.Data.Runtime

open System
open System.ComponentModel
open System.Globalization
open System.IO
open System.Text
open System.Xml
open FSharp.Data
open FSharp.Data.Runtime
open FSharp.Data.Html
open System.Text.RegularExpressions

#nowarn "10001"

// --------------------------------------------------------------------------------------

/// [omit]
type HtmlTableCell = 
    | Cell of bool * string
    | Empty
    member x.IsHeader =
        match x with
        | Empty -> true
        | Cell(h, _) -> h
    member x.Data = 
        match x with
        | Empty -> ""
        | Cell(_, d) -> d

/// [omit]
type HtmlTable = 
    { Name : string
      Headers : string []
      Rows :  string [] [] }

// --------------------------------------------------------------------------------------

/// Helper functions called from the generated code for working with HTML tables
module HtmlRuntime =

    open Html

    let private getTableName defaultName nameSet (element:HtmlNode) = 
        let tryGetName' choices =
            choices
            |> List.tryPick (fun (attrName) -> 
                match element.TryGetAttribute attrName with
                | Some(HtmlAttribute(_,value)) -> Some <| value
                | None -> None
            )
        let deriveFromSibling element = 
            let isHeading s =
                let name = HtmlNode.name s
                Regex.IsMatch(name, """h\d""")
            HtmlNode.tryFindPrevious isHeading element

        match deriveFromSibling element with
        | Some(e) when not(Set.contains e.InnerText nameSet) -> e.InnerText
        | _ ->
                match element.Descendants ["caption"] with
                | [] ->
                     match tryGetName' [ "id"; "name"; "title"; "summary"] with
                     | Some(name) when not(Set.contains name nameSet) -> name
                     | _ -> defaultName
                | h :: _ -> h.InnerText

    let private parseTable (index, nameSet) (table:HtmlNode) = 
        let rows = table.Descendants(["tr"], true, false) |> List.mapi (fun i r -> i,r)
        if rows.Length <= 1 
        then None
        else
            let cells = rows |> List.map (fun (_,r) -> r.Elements ["td"; "th"] |> List.mapi (fun i e -> (i,e)))
            let width = (cells |> List.maxBy (fun x -> x.Length)).Length
            let res = Array.init rows.Length  (fun _ -> Array.init width (fun _ -> Empty))
            for rowindex, _ in rows do
                for colindex, cell in cells.[rowindex] do
                    let rowSpan, colSpan = (max 1 (cell.GetAttributeValue(0,Int32.TryParse,"rowspan"))) - 1,(max 1 (cell.GetAttributeValue(0,Int32.TryParse,"colspan"))) - 1
                    let data =
                        let getContents contents = String.Join(" ", contents |> List.map (fun (x:HtmlNode) -> x.InnerText)).Trim()
                        match cell with
                        | HtmlElement(_,"td", _, contents) -> Cell (false, getContents contents)
                        | HtmlElement(_,"th", _, contents) -> Cell (true, getContents contents)
                        | _ -> Empty
                    let col_i = ref colindex
                    while res.[rowindex].[!col_i] <> Empty do incr(col_i)
                    for j in [!col_i..(!col_i + colSpan)] do
                        for i in [rowindex..(rowindex + rowSpan)] do
                            if i < rows.Length && j < width
                            then res.[i].[j] <- data

            let (startIndex, headers) = 
                if res.[0] |> Array.forall (fun r -> r.IsHeader) 
                then 1, res.[0] |> Array.map (fun x -> x.Data)
                else HtmlInference.inferHeaders (res |> Array.map (Array.map (fun x -> x.Data)))
            
            let headers = 
                if headers.Length = 0
                then res.[0] |> Array.mapi (fun i _ -> "Column" + (string i))
                else headers
                       

            { Name = (getTableName ("Table_" + (string index)) nameSet table)
              Headers = headers
              Rows = res.[startIndex..] |> Array.map (Array.map (fun x -> x.Data)) } |> Some

    let getTables includeLayoutTables (doc:HtmlDocument) =
        let tableElements = 
            doc.Descendants(["table"],true)
            |> (fun x -> if includeLayoutTables 
                         then x 
                         else x |> List.filter (fun e -> (e.HasAttribute("cellspacing", "0") && e.HasAttribute("cellpadding", "0")) |> not)
                )
        let (_,_,tables) =
            tableElements
            |> List.fold (fun (index,names,tables) node -> 
                            match parseTable (index,names) node with
                            | Some(table) -> 
                                (index + 1, Set.add table.Name names, table::tables)
                            | None -> (index + 1, names, tables)
                         ) (0, Set.empty, [])
        tables |> List.rev

    let formatTable (data:HtmlTable) =
        let sb = StringBuilder()
        use wr = new StringWriter(sb) 
        let data = array2D ((data.Headers |> List.ofArray) :: (data.Rows |> Array.map (List.ofArray) |> List.ofArray))    
        let rows = data.GetLength(0)
        let columns = data.GetLength(1)
        let widths = Array.zeroCreate columns 
        data |> Array2D.iteri (fun _ c cell ->
            widths.[c] <- max (widths.[c]) (cell.Length))
        for r in 0 .. rows - 1 do
            for c in 0 .. columns - 1 do
                wr.Write(data.[r,c].PadRight(widths.[c] + 1))
            wr.WriteLine()
        sb.ToString()

/// [omit]
type TypedHtmlDocument internal (tables:Map<string,HtmlTable>) =

    [<EditorBrowsableAttribute(EditorBrowsableState.Never)>]
    [<CompilerMessageAttribute("This method is not intended for use from F#.", 10001, IsHidden=true, IsError=false)>]
    static member Create(includeLayoutTables:bool, reader:TextReader) =
        let tables = 
            reader 
            |> HtmlDocument.Load
            |> HtmlRuntime.getTables includeLayoutTables
            |> List.map (fun table -> table.Name, table) 
            |> Map.ofList
        TypedHtmlDocument tables

    [<EditorBrowsableAttribute(EditorBrowsableState.Never)>]
    [<CompilerMessageAttribute("This method is not intended for use from F#.", 10001, IsHidden=true, IsError=false)>]
    member __.GetTable(id:string) = 
       tables |> Map.find id

/// [omit]
and HtmlTable<'rowType> internal (name:string, header:string[], values:'rowType[]) =

    member x.Name with get() = name
    member x.Headers with get() = header
    member x.Rows with get() = values

    [<EditorBrowsableAttribute(EditorBrowsableState.Never)>]
    [<CompilerMessageAttribute("This method is not intended for use from F#.", 10001, IsHidden=true, IsError=false)>]
    static member Create(rowConverter:Func<string[],'rowType>, doc:TypedHtmlDocument, id:string) =
       let table = doc.GetTable id
       HtmlTable<_>(table.Name, table.Headers, Array.map rowConverter.Invoke table.Rows) 