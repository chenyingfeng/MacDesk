using System;
using System.Collections.Generic;
using System.Linq;
namespace StageManager.Services;
// Selection order is independent of HWND/capture lifetime. All callers run on the dispatcher.
internal sealed class StageSelection<T> where T:class
{
    private readonly List<T> _items=new();
    private readonly Func<T,string> scope;
    internal string ActiveScope {get;set;}="";
    internal StageSelection(Func<T,string>? scope=null) {this.scope=scope??(_=>"");}
    internal IReadOnlyList<T> Items=>_items.ToArray();
    internal IReadOnlyList<T> ActiveItems=>_items.Where(x=>scope(x)==ActiveScope).ToArray();
    internal T? Current=>ActiveItems.LastOrDefault();
    internal bool Contains(T item)=>_items.Contains(item);
    internal void Seed(T item) {ActiveScope=scope(item);if(!_items.Contains(item)) _items.Add(item);}
    internal void Select(T? item,bool keepOthers) {
        if(item!=null)ActiveScope=scope(item);
        if(item==null || !keepOthers) _items.RemoveAll(x=>scope(x)==ActiveScope);
        if(item!=null) {_items.Remove(item);_items.Add(item);}
    }
    internal void Remove(T item)=>_items.Remove(item);
}
