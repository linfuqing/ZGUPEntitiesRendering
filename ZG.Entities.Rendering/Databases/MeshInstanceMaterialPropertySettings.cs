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
            Texture, 
            Vector,
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
                    case PropertyFormat.Vector:
                        return x.GetVector(propertyName) == y.GetVector(propertyName);
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

            [Tooltip("�ؼ�����ͬ���ж�Ϊͬһ�ֲ���")]
            public string[] keywordConditions;

            [Tooltip("������ͬ���ж�Ϊͬһ�ֲ���")]
            public PropertyCondition[] propertyConditions;

            public PropertyOverride[] propertyOverrides;

            public bool Apply(Material material, Action<string, Type, ShaderPropertyType, Vector4> overrideValues)
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
                    ShaderPropertyType shaderPropertyType;
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
                            shaderPropertyType = shader.GetPropertyType(propertyIndex);
                            switch (shaderPropertyType)
                            {
                                case ShaderPropertyType.Vector:
                                case ShaderPropertyType.Color:
                                    vector = material.GetVector(attribute.Name);
                                    overrideValues(attribute.Name, type, shaderPropertyType, vector);
                                    break;
                                case ShaderPropertyType.Float:
                                case ShaderPropertyType.Range:
                                    vector.x = material.GetFloat(attribute.Name);
                                    vector.y = 0.0f;
                                    vector.z = 0.0f;
                                    vector.w = 0.0f;
                                    overrideValues(attribute.Name, type, shaderPropertyType, vector);
                                    break;
                                case ShaderPropertyType.Int:
                                    vector.x = Unity.Mathematics.math.asfloat(material.GetInt(attribute.Name));
                                    vector.y = 0.0f;
                                    vector.z = 0.0f;
                                    vector.w = 0.0f;
                                    overrideValues(attribute.Name, type, shaderPropertyType, vector);
                                    break;
                            }
                        }
                    }
                }

                return true;
            }
        }

        public MaterialOverride[] materialOverrides;

        public Material Override(Material material, Action<string, Type, ShaderPropertyType, Vector4> overrideValues)
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