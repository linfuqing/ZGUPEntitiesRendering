using System;
using System.Reflection;
using System.Collections.Generic;
using Unity.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace ZG
{
    [CreateAssetMenu(menuName = "ZG/Mesh Instance/Material Property Settings", fileName = "MeshInstanceMaterialPropertySettings")]
    public class MeshInstanceMaterialPropertySettings : ScriptableObject
    {
        public enum PropertyFormat
        {
            Texture
        }

        [Serializable]
        public struct PropertyCondition
        {
            public string propertyName;

            public PropertyFormat format;

            public bool Check(Material x, Material y)
            {
                switch (format)
                {
                    case PropertyFormat.Texture:
                        return x.GetTexture(propertyName) == y.GetTexture(propertyName);
                }

                return false;
            }
        }

        [Serializable]
        public struct PropertyOverride
        {
            [Type(typeof(MaterialPropertyAttribute))]
            public string typeName;
        }

        [Serializable]
        public struct MaterialOverride
        {
            public Material sharedMaterial;

            [Tooltip("关键字相同则判定为同一种材质")]
            public string[] keywordConditions;

            [Tooltip("属性相同则判定为同一种材质")]
            public PropertyCondition[] propertyConditions;

            public PropertyOverride[] propertyOverrides;

            public bool Apply(Material material, Action<Type, float[]> overrideValues)
            {
                var shader = material.shader;
                if (shader != sharedMaterial.shader)
                    return false;

                if (keywordConditions != null)
                {
                    foreach (var keywordCondition in keywordConditions)
                    {
                        if (material.IsKeywordEnabled(keywordCondition) != sharedMaterial.IsKeywordEnabled(keywordCondition))
                            return false;
                    }
                }

                if (propertyConditions != null)
                {
                    foreach (var propertyCondition in propertyConditions)
                    {
                        if (!propertyCondition.Check(material, sharedMaterial))
                            return false;
                    }
                }

                if(propertyOverrides != null)
                {
                    int propertyIndex;
                    Vector4 vector;
                    Type type;
                    IEnumerable<MaterialPropertyAttribute> attributes;
                    foreach(var propertyOverride in propertyOverrides)
                    {
                        type = Type.GetType(propertyOverride.typeName);
                        if (type == null)
                            continue;

                        attributes = type.GetCustomAttributes<MaterialPropertyAttribute>();
                        if (attributes == null)
                            continue;

                        foreach(var attribute in attributes)
                        {
                            propertyIndex = shader.FindPropertyIndex(attribute.Name);
                            switch(shader.GetPropertyType(propertyIndex))
                            {
                                case ShaderPropertyType.Vector:
                                case ShaderPropertyType.Color:
                                    vector = material.GetVector(attribute.Name);
                                    overrideValues(type, new float[] { vector.x, vector.y, vector.z, vector.w });
                                    break;
                                case ShaderPropertyType.Float:
                                case ShaderPropertyType.Range:
                                    overrideValues(type, new float[] { material.GetFloat(attribute.Name) });
                                    break;
                                case ShaderPropertyType.Int:
                                    overrideValues(type, new float[] { Unity.Mathematics.math.asfloat(material.GetInt(attribute.Name)) });
                                    break;
                            }
                        }
                    }
                }

                return true;
            }
        }

        public MaterialOverride[] materialOverrides;

        public Material Override(Material material, Action<Type, float[]> overrideValues)
        {
            foreach(var materialOverride in materialOverrides)
            {
                if(materialOverride.Apply(material, overrideValues))
                    return materialOverride.sharedMaterial;
            }

            return material;
        }
    }
}