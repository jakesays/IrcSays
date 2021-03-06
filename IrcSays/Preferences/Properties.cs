﻿// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Xaml;
using System.Xml;
using System.Xml.Linq;
using IrcSays.Utility;

namespace IrcSays.Preferences
{
	/// <summary>
	///     This interface flags an object beeing "mementocapable". This means that the
	///     state of the object could be saved to an <see cref="Properties" /> object
	///     and set from a object from the same class.
	///     This is used to save and restore the state of GUI objects.
	/// </summary>
	/// <remarks>
	///     This interface is used as a [ViewContentService]
	/// </remarks>
	public interface IMementoCapable
	{
		/// <summary>
		///     Creates a new memento from the state.
		/// </summary>
		Properties CreateMemento();

		/// <summary>
		///     Sets the state to the given memento.
		/// </summary>
		void SetMemento(Properties memento);
	}

	/// <summary>
	///     A container for settings - key/value pairs where keys are strings, and values are arbitrary objects.
	///     Instances of this class are thread-safe.
	/// </summary>
	public sealed class Properties : INotifyPropertyChanged, ICloneable
	{
		/// <summary>
		///     Gets the version number of the XML file format.
		/// </summary>
		public static readonly Version FileVersion = new Version(2, 0, 0);

		// Properties instances form a tree due to the nested properties containers.
		// All nodes in such a tree share the same syncRoot in order to simplify synchronization.
		// When an existing node is added to a tree, its syncRoot needs to change.
		private object _syncRoot;
		private Properties _parent;
		// Objects in the dictionary are one of:
		// - string: value stored using TypeConverter
		// - XElement: serialized object
		// - object[]: a stored list (array elements are null, string or XElement)
		// - Properties: nested properties container
		private readonly Dictionary<string, object> _propertyDict = new Dictionary<string, object>();


		public Properties()
		{
			_syncRoot = new object();
		}

		private Properties(Properties parent)
		{
			_parent = parent;
			_syncRoot = parent._syncRoot;
		}


		public event PropertyChangedEventHandler PropertyChanged;

		private void OnPropertyChanged(string key)
		{
			var handler = Volatile.Read(ref PropertyChanged);
			if (handler != null)
			{
				handler(this, new PropertyChangedEventArgs(key));
			}
		}


		private bool _isDirty;

		/// <summary>
		///     Gets/Sets whether this properties container is dirty.
		///     IsDirty automatically gets set to <c>true</c> when a property in this container (or a nested container)
		///     changes.
		/// </summary>
		public bool IsDirty
		{
			get { return _isDirty; }
			set
			{
				lock (_syncRoot)
				{
					if (value)
					{
						MakeDirty();
					}
					else
					{
						CleanDirty();
					}
				}
			}
		}

		private void MakeDirty()
		{
			// called within syncroot
			if (!_isDirty)
			{
				_isDirty = true;
				if (_parent != null)
				{
					_parent.MakeDirty();
				}
			}
		}

		private void CleanDirty()
		{
			if (_isDirty)
			{
				_isDirty = false;
				foreach (var properties in _propertyDict.Values.OfType<Properties>())
				{
					properties.CleanDirty();
				}
			}
		}


		/// <summary>
		///     Gets the keys that are in use by this properties container.
		/// </summary>
		public IReadOnlyList<string> Keys
		{
			get
			{
				lock (_syncRoot)
				{
					return _propertyDict.Keys.ToArray();
				}
			}
		}

		/// <summary>
		///     Gets whether this properties instance contains any entry (value, list, or nested container)
		///     with the specified key.
		/// </summary>
		public bool Contains(string key)
		{
			lock (_syncRoot)
			{
				return _propertyDict.ContainsKey(key);
			}
		}


		/// <summary>
		///     Retrieves a string value from this Properties-container.
		///     Using this indexer is equivalent to calling <c>Get(key, string.Empty)</c>.
		/// </summary>
		public string this[string key]
		{
			get
			{
				lock (_syncRoot)
				{
					object val;
					_propertyDict.TryGetValue(key, out val);
					return val as string ?? string.Empty;
				}
			}
			set { Set(key, value); }
		}

		/// <summary>
		///     Retrieves a single element from this Properties-container.
		/// </summary>
		/// <param name="key">Key of the item to retrieve</param>
		/// <param name="defaultFactory">Factory that creates the default value to be returned if the key is not present.</param>
		public TValue Get<TValue>(string key, Func<TValue> defaultFactory)
		{
			lock (_syncRoot)
			{
				object val;
				if (_propertyDict.TryGetValue(key, out val))
				{
					try
					{
						return (TValue) Deserialize(val, typeof(TValue));
					}
					catch (SerializationException ex)
					{
						return defaultFactory();
					}
				}
				return defaultFactory();
			}
		}

		/// <summary>
		///     Retrieves a single element from this Properties-container.
		/// </summary>
		/// <param name="key">Key of the item to retrieve</param>
		/// <param name="defaultValue">Default value to be returned if the key is not present.</param>
		public TValue Get<TValue>(string key, TValue defaultValue)
		{
			return Get(key, () => defaultValue);
		}

		/// <summary>
		///     Sets a single element in this Properties-container.
		///     The element will be serialized using a TypeConverter if possible, or XAML serializer otherwise.
		/// </summary>
		/// <remarks>Setting a key to <c>null</c> has the same effect as calling <see cref="Remove" />.</remarks>
		public TValue Set<TValue>(string key, TValue value)
		{
			var serializedValue = Serialize(value, typeof (TValue), key);
			SetSerializedValue(key, serializedValue);
			
			return value;
		}

		private void SetSerializedValue(string key, object serializedValue)
		{
			if (serializedValue == null)
			{
				Remove(key);
				return;
			}
			lock (_syncRoot)
			{
				object oldValue;
				if (_propertyDict.TryGetValue(key, out oldValue))
				{
					if (Equals(serializedValue, oldValue))
					{
						return;
					}
					HandleOldValue(oldValue);
				}
				_propertyDict[key] = serializedValue;
			}
			OnPropertyChanged(key);
		}


		/// <summary>
		///     Retrieves the list of items stored with the specified key.
		///     If no entry with the specified key exists, this method returns an empty list.
		/// </summary>
		/// <remarks>
		///     This method returns a copy of the list used internally; you need to call
		///     <see cref="SetList" /> if you want to store the changed list.
		/// </remarks>
		public IReadOnlyList<T> GetList<T>(string key)
		{
			lock (_syncRoot)
			{
				object val;
				if (_propertyDict.TryGetValue(key, out val))
				{
					var serializedArray = val as object[];
					if (serializedArray != null)
					{
						try
						{
							var array = new T[serializedArray.Length];
							for (var i = 0; i < array.Length; i++)
							{
								array[i] = (T) Deserialize(serializedArray[i], typeof (T));
							}
							return array;
						}
						catch (XamlObjectWriterException ex)
						{
//							LoggingService.Warn(ex);
						}
						catch (NotSupportedException ex)
						{
//							LoggingService.Warn(ex);
						}
					}
//					else {
//						LoggingService.Warn("Properties.GetList(" + key + ") - this entry is not a list");
//					}
				}
				return new T[0];
			}
		}

		/// <summary>
		///     Sets a list of elements in this Properties-container.
		///     The elements will be serialized using a TypeConverter if possible, or XAML serializer otherwise.
		/// </summary>
		/// <remarks>Passing <c>null</c> or an empty list as value has the same effect as calling <see cref="Remove" />.</remarks>
		public void SetList<T>(string key, IEnumerable<T> value)
		{
			if (value == null)
			{
				Remove(key);
				return;
			}
			var array = value.ToArray();
			if (array.Length == 0)
			{
				Remove(key);
				return;
			}
			var serializedArray = new object[array.Length];
			for (var i = 0; i < array.Length; i++)
			{
				serializedArray[i] = Serialize(array[i], typeof (T), null);
			}
			SetSerializedValue(key, serializedArray);
		}

		private object Serialize(object value, Type sourceType, string key)
		{
			if (value == null)
			{
				return null;
			}
			var c = TypeDescriptor.GetConverter(sourceType);
			if (c != null &&
				c.CanConvertTo(typeof (string)) &&
				c.CanConvertFrom(typeof (string)))
			{
				return c.ConvertToInvariantString(value);
			}

			var element = new XElement("SerializedObject");
			if (key != null)
			{
				element.Add(new XAttribute("key", key));
			}
			using (var xmlWriter = element.CreateWriter())
			{
				XamlServices.Save(xmlWriter, value);
			}
			return element;
		}

		private object Deserialize(object serializedVal, Type targetType)
		{
			if (serializedVal == null)
			{
				return null;
			}
			var element = serializedVal as XElement;
			if (element != null)
			{
				using (var xmlReader = element.Elements().Single().CreateReader())
				{
					return XamlServices.Load(xmlReader);
				}
			}
			var text = serializedVal as string;
			if (text == null)
			{
				throw new InvalidOperationException("Cannot read a properties container as a single value");
			}
			var c = TypeDescriptor.GetConverter(targetType);
			return c.ConvertFromInvariantString(text);
		}


		/// <summary>
		///     Removes the entry (value, list, or nested container) with the specified key.
		/// </summary>
		public bool Remove(string key)
		{
			var removed = false;
			lock (_syncRoot)
			{
				object oldValue;
				if (_propertyDict.TryGetValue(key, out oldValue))
				{
					removed = true;
					HandleOldValue(oldValue);
					MakeDirty();
					_propertyDict.Remove(key);
				}
			}
			if (removed)
			{
				OnPropertyChanged(key);
			}
			return removed;
		}


		/// <summary>
		///     Gets the parent property container.
		/// </summary>
		public Properties Parent
		{
			get
			{
				lock (_syncRoot)
				{
					return _parent;
				}
			}
		}

		/// <summary>
		///     Retrieves a nested property container; creating a new one on demand.
		///     Multiple calls to this method will return the same instance (unless the entry at this key
		///     is overwritten by one of the Set-methods).
		///     Changes performed on the nested container will be persisted together with the parent container.
		/// </summary>
		public Properties NestedProperties(string key)
		{
			var isNewContainer = false;
			Properties result;
			lock (_syncRoot)
			{
				object oldValue;
				_propertyDict.TryGetValue(key, out oldValue);
				result = oldValue as Properties;
				if (result == null)
				{
					result = new Properties(this);
					_propertyDict[key] = result;
					isNewContainer = true;
				}
			}
			if (isNewContainer)
			{
				OnPropertyChanged(key);
			}
			return result;
		}

		private void HandleOldValue(object oldValue)
		{
			var p = oldValue as Properties;
			if (p != null)
			{
				Debug.Assert(p._parent == this);
				p._parent = null;
			}
		}

		/// <summary>
		///     Attaches the specified properties container as nested properties.
		///     This method is intended to be used in conjunction with the <see cref="IMementoCapable" /> pattern
		///     where a new unattached properties container is created and then later attached to a parent container.
		/// </summary>
		public void SetNestedProperties(string key, Properties properties)
		{
			if (properties == null)
			{
				Remove(key);
				return;
			}
			lock (_syncRoot)
			{
				for (var ancestor = this; ancestor != null; ancestor = ancestor._parent)
				{
					if (ancestor == properties)
					{
						throw new InvalidOperationException("Cannot add a properties container to itself.");
					}
				}

				object oldValue;
				if (_propertyDict.TryGetValue(key, out oldValue))
				{
					if (oldValue == properties)
					{
						return;
					}
					HandleOldValue(oldValue);
				}
				lock (properties._syncRoot)
				{
					if (properties._parent != null)
					{
						throw new InvalidOperationException("Cannot attach nested properties that already have a parent.");
					}
					MakeDirty();
					properties.SetSyncRoot(_syncRoot);
					properties._parent = this;
					_propertyDict[key] = properties;
				}
			}
			OnPropertyChanged(key);
		}

		private void SetSyncRoot(object newSyncRoot)
		{
			_syncRoot = newSyncRoot;
			foreach (var properties in _propertyDict.Values.OfType<Properties>())
			{
				properties.SetSyncRoot(newSyncRoot);
			}
		}


		/// <summary>
		///     Creates a deep clone of this Properties container.
		/// </summary>
		public Properties Clone()
		{
			lock (_syncRoot)
			{
				return CloneWithParent(null);
			}
		}

		private Properties CloneWithParent(Properties parent)
		{
			var copy = parent != null ? new Properties(parent) : new Properties();
			foreach (var pair in _propertyDict)
			{
				var child = pair.Value as Properties;
				if (child != null)
				{
					copy._propertyDict.Add(pair.Key, child.CloneWithParent(copy));
				}
				else
				{
					copy._propertyDict.Add(pair.Key, pair.Value);
				}
			}
			return copy;
		}

		object ICloneable.Clone()
		{
			return Clone();
		}


		internal static Properties ReadFromAttributes(XmlReader reader)
		{
			var properties = new Properties();
			if (reader.HasAttributes)
			{
				for (var i = 0; i < reader.AttributeCount; i++)
				{
					reader.MoveToAttribute(i);
					// some values are frequently repeated (e.g. type="MenuItem"),
					// so we also use the NameTable for attribute values
					// (XmlReader itself only uses it for attribute names)
					var val = reader.NameTable.Add(reader.Value);
					properties[reader.Name] = val;
				}
				reader.MoveToElement(); //Moves the reader back to the element node.
			}
			return properties;
		}


		public static Properties Load(FileName fileName)
		{
			return Load(XDocument.Load(fileName).Root);
		}

		public static Properties Load(XElement element)
		{
			var properties = new Properties();
			properties.LoadContents(element.Elements());
			return properties;
		}

		private void LoadContents(IEnumerable<XElement> elements)
		{
			foreach (var element in elements)
			{
				var key = (string) element.Attribute("key");
				if (key == null)
				{
					continue;
				}
				switch (element.Name.LocalName)
				{
					case "Property":
						_propertyDict[key] = element.Value;
						break;
					case "Array":
						_propertyDict[key] = LoadArray(element.Elements());
						break;
					case "SerializedObject":
						_propertyDict[key] = new XElement(element);
						break;
					case "Properties":
						var child = new Properties(this);
						child.LoadContents(element.Elements());
						_propertyDict[key] = child;
						break;
				}
			}
		}

		private static object[] LoadArray(IEnumerable<XElement> elements)
		{
			var result = new List<object>();
			foreach (var element in elements)
			{
				switch (element.Name.LocalName)
				{
					case "Null":
						result.Add(null);
						break;
					case "Element":
						result.Add(element.Value);
						break;
					case "SerializedObject":
						result.Add(new XElement(element));
						break;
				}
			}
			return result.ToArray();
		}

		public void Save(FileName fileName)
		{
			new XDocument(Save()).Save(fileName);
		}

		public XElement Save()
		{
			lock (_syncRoot)
			{
				return new XElement("Properties", SaveContents());
			}
		}

		private IReadOnlyList<XElement> SaveContents()
		{
			var result = new List<XElement>();
			foreach (var pair in _propertyDict)
			{
				var key = new XAttribute("key", pair.Key);
				var child = pair.Value as Properties;
				if (child != null)
				{
					var contents = child.SaveContents();
					if (contents.Count > 0)
					{
						result.Add(new XElement("Properties", key, contents));
					}
				}
				else if (pair.Value is object[])
				{
					var array = (object[]) pair.Value;
					var elements = new XElement[array.Length];
					for (var i = 0; i < array.Length; i++)
					{
						var obj = array[i] as XElement;
						if (obj != null)
						{
							elements[i] = new XElement(obj);
						}
						else if (array[i] == null)
						{
							elements[i] = new XElement("Null");
						}
						else
						{
							elements[i] = new XElement("Element", (string) array[i]);
						}
					}
					result.Add(new XElement("Array", key, elements));
				}
				else if (pair.Value is XElement)
				{
					result.Add(new XElement((XElement) pair.Value));
				}
				else
				{
					result.Add(new XElement("Property", key, (string) pair.Value));
				}
			}
			return result;
		}
	}
}